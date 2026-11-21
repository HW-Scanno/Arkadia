using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Archive;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>What Deep Hashing did with one <c>.rvz</c> candidate.</summary>
public enum RvzDeepHashDisposition
{
    /// <summary>Not handled (not an RVZ container, unreadable, or its PHYSICAL hash matches the DAT) — the ordinary pipeline processes it unchanged.</summary>
    HandedBack,
    /// <summary>Logical ISO verified against a unique wanted requirement; RVZ committed to the local archive.</summary>
    Adopted,
    /// <summary>No DAT requirement matches the logical ISO → moved to incoming-skip (existing skip behaviour).</summary>
    NoMatch,
    /// <summary>The logical ISO matches more than one release → never assigned arbitrarily; moved to incoming-skip.</summary>
    Ambiguous,
    /// <summary>The unique matching release is unwanted → moved to incoming-skip (UNWANTED WINS).</summary>
    Unwanted,
    /// <summary>The unique matching release is already PRESENT (DB-authoritative: present + derived artifact) → the redundant
    /// incoming RVZ is deleted (existing <c>duplicate-deleted</c> semantics); the archived artifact is never touched.</summary>
    AlreadySatisfied,
    /// <summary>The matching release is not a single-file RVZ-eligible release → moved to incoming-skip.</summary>
    Unsupported,
    /// <summary>DolphinTool failed / produced malformed output → file preserved in place (not a mismatch).</summary>
    ToolFailed,
    /// <summary>Matched + wanted, but an archive collision / transfer / commit safeguard refused it → file preserved in place.</summary>
    CommitBlocked,
}

/// <summary>Per-run configuration (resolved by the caller from the leaf's persisted configuration).</summary>
public sealed record RvzDeepHashSettings
{
    public required string                   AppRoot          { get; init; }
    public required string                   PlatformId       { get; init; }
    public required string                   DatLineId        { get; init; }
    public required string                   SkipDir          { get; init; }
    public required ArchiveDatLineOutputForm ArchiveForm      { get; init; }
    /// <summary>True when the DAT rom (by its name/extension) is stored with <c>rvz_compression</c> on this leaf.</summary>
    public required Func<string, bool>       IsRvzEligibleRom { get; init; }
    public required Func<string, string>     SafeFileName     { get; init; }
    /// <summary>One <c>DolphinTool verify</c> run for a candidate (injectable for tests).</summary>
    public required Func<string, DolphinVerifyResult> VerifyLogical { get; init; }
    /// <summary>Same-filesystem predicate (injectable for tests). Default: <see cref="StagingHelpers.SameVolume"/>.</summary>
    public Func<string, string, bool>        SameFilesystem   { get; init; } = StagingHelpers.SameVolume;
    /// <summary>Cross-filesystem copy (injectable for tests). Default: <c>File.Copy(src, dest, overwrite: false)</c>.</summary>
    public Action<string, string>            CrossFilesystemCopy { get; init; } = (src, dest) => File.Copy(src, dest, overwrite: false);
}

/// <summary>Outcome of the Deep Hashing phase for the ordinary pipeline's bookkeeping.</summary>
public sealed class RvzDeepHashOutcome
{
    /// <summary>Candidates fully handled here — the ordinary matcher/staging/Phase 8 must not process them again.</summary>
    public HashSet<string> Consumed { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Adopted candidate path → release id (Phase 9 archive association).</summary>
    public Dictionary<string, string> AdoptedReleaseByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Redundant candidate path → already-PRESENT release id (deleted as a duplicate; Phase 9 association).</summary>
    public Dictionary<string, string> DuplicateReleaseByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Releases whose matched RVZ could not be committed (collision/transfer/commit) — counted as failures.</summary>
    public HashSet<string> FailedReleaseIds { get; } = new(StringComparer.Ordinal);
    /// <summary>Candidates whose DolphinTool run failed (tool error, not a mismatch) — preserved, counted as failures.</summary>
    public HashSet<string> ToolFailedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The run's <see cref="IngestionResult.TransformsFailed"/> value: ordinary failed releases ∪ deep-hash commit
    /// failures that did not end present this run, plus one per preserved DolphinTool failure.
    /// </summary>
    public int CombinedFailureCount(IReadOnlyCollection<string> transformFailedReleases, IReadOnlySet<string> successfulReleaseIds)
        => transformFailedReleases
               .Union(FailedReleaseIds.Where(id => !successfulReleaseIds.Contains(id)), StringComparer.Ordinal)
               .Count()
           + ToolFailedPaths.Count;
    public Dictionary<string, RvzDeepHashDisposition> Dispositions { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Deep Hashing (RVZ-SUPPORT-01, Phase B): identifies existing <c>.rvz</c> files by the LOGICAL disc image DolphinTool
/// reconstructs, and adopts a verified one directly as the release's derived artifact — no temporary ISO, no
/// decompression to disk, no recompression, the incoming RVZ is never modified.
///
/// <para><b>Model.</b> Content identity = the DAT ISO identity (<c>sha1:&lt;dat sha1&gt;</c>). Source artifact = the
/// LOGICAL ISO: its verified logical SHA1/MD5/CRC32 and logical size (no file in <c>source/</c> is created or
/// implied — <c>source_artifacts</c> has no path). Derived artifact = the physical RVZ (physical SHA1/MD5/CRC32 +
/// physical size) under <c>rvz_compression</c>, Tier A (verified to reconstruct the source ISO). The result is the
/// same DB shape as an ISO converted by <see cref="RvzConversion"/>.</para>
///
/// <para><b>I/O per candidate.</b> 72-byte header read; ONE physical pass (SHA1+MD5+CRC32 together — no separate MD5
/// pass), reused for the physical-match check and the derived-artifact registration; at most ONE DolphinTool logical
/// pass (skipped when the header's logical size cannot match any eligible requirement); then a native
/// <see cref="File.Move(string,string,bool)"/> on the same filesystem (no copy, no rehash). Only across filesystems is the
/// file copied, and that copy is accepted only when its SHA1 equals the physical SHA1 already computed.</para>
///
/// <para><b>Acceptance.</b> Only a unique, wanted, not-yet-satisfied, single-file RVZ-eligible release whose DAT SHA1
/// equals the logical SHA1, whose DAT size (when declared) equals the logical size, and whose DAT MD5/CRC32 (when
/// declared) equal the logical digests. Never on a successful verify alone, never on CRC32.</para>
/// </summary>
public static class RvzDeepHashing
{
    public static bool IsCandidate(string path) =>
        string.Equals(Path.GetExtension(path), RvzIds.OutputExtension, StringComparison.OrdinalIgnoreCase);

    public static RvzDeepHashOutcome Run(
        RvzDeepHashSettings                                   s,
        IReadOnlyList<string>                                 candidates,
        DatLineStore                                          store,
        IReadOnlyList<ReleaseRecord>                          allReleases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>>  allReleaseFiles,
        IngestionResult                                       result,
        IProgress<IngestionProgress>                          progress)
    {
        var outcome = new RvzDeepHashOutcome();
        if (candidates.Count == 0) return outcome;

        // Same matchable identity as the ordinary matcher (non-outdated releases, Physical requirements only).
        var needs    = LeafIngestionNeeds.Build(s.DatLineId, allReleases, allReleaseFiles, Array.Empty<string>());
        var releases = needs.Releases;

        // Logical-size pre-filter: sizes of every requirement a logical match could legitimately hit (all statuses, so
        // unwanted/satisfied matches are still classified). Disabled if any such requirement lacks a declared size.
        var eligibleSizes = new HashSet<long>();
        bool sizeFilter   = true;
        foreach (var targets in needs.Sha1Index.Values)
            foreach (var (rid, rom) in targets)
            {
                if (!s.IsRvzEligibleRom(rom)) continue;
                if (needs.ExpectedSizeIndex.TryGetValue($"{rid}|{rom}", out var sz)) eligibleSizes.Add(sz);
                else sizeFilter = false;
            }

        int n = 0;
        foreach (var path in candidates)
        {
            n++;
            progress.Report(new IngestionProgress
            {
                PhaseText = $"Deep hashing RVZ ({n}/{candidates.Count})…", IsIndeterminate = false,
                Total = candidates.Count, Processed = n - 1,
            });
            var d = Process(s, path, store, needs, releases, allReleaseFiles, eligibleSizes, sizeFilter, outcome, result, progress);
            outcome.Dispositions[path] = d;
            if (d != RvzDeepHashDisposition.HandedBack) outcome.Consumed.Add(path);
        }
        return outcome;
    }

    private static RvzDeepHashDisposition Process(
        RvzDeepHashSettings                                  s,
        string                                               path,
        DatLineStore                                         store,
        LeafIngestionNeeds                                   needs,
        IReadOnlyDictionary<string, ReleaseRecord>           releases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>> allReleaseFiles,
        HashSet<long>                                        eligibleSizes,
        bool                                                 sizeFilter,
        RvzDeepHashOutcome                                   outcome,
        IngestionResult                                      result,
        IProgress<IngestionProgress>                         progress)
    {
        var name = Path.GetFileName(path);
        void Op(string action, string detail) => Report(result, progress, new IngestionOperation(name, action, detail));

        // ── 1. Header (72 bytes): only real RVZ containers enter Deep Hashing ──
        if (!RvzHeader.TryRead(path, out var header, out var headerError))
        {
            Op("deep-hash-not-applicable", $"{headerError} — processed by the ordinary pipeline");
            return RvzDeepHashDisposition.HandedBack;
        }

        // ── 2. ONE physical pass (SHA1+MD5+CRC32) — reused for matching and registration ──
        PhysicalHashes phys;
        try { phys = PhysicalHashes.Compute(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Op("deep-hash-not-applicable", $"unreadable ({ex.Message}) — processed by the ordinary pipeline");
            return RvzDeepHashDisposition.HandedBack;
        }

        // A DAT that identifies the RVZ file PHYSICALLY keeps the ordinary SHA1→MD5 matching semantics.
        if (needs.Sha1Index.ContainsKey(phys.Sha1) || needs.Md5Index.ContainsKey(phys.Md5))
        {
            Op("deep-hash-physical-match", "DAT identifies this RVZ physically — processed by the ordinary pipeline");
            return RvzDeepHashDisposition.HandedBack;
        }

        // ── 3. Logical-size pre-filter (no tool run when no eligible requirement can match) ──
        if (sizeFilter && !eligibleSizes.Contains(header.LogicalIsoSize))
        {
            Op("rvz-no-match", $"logical size {header.LogicalIsoSize} matches no RVZ-eligible DAT entry");
            MoveToSkip(s, path, "skip", "skip-failed", result, progress);
            result.FilesSkipped++;
            return RvzDeepHashDisposition.NoMatch;
        }

        // ── 4. Logical identification — exactly one DolphinTool pass ──
        var verify = s.VerifyLogical(path);
        if (!verify.Success || verify.Hashes is null)
        {
            Op("deep-hash-failed", $"{verify.Error} — file preserved in incoming (tool failure, not a hash mismatch)");
            outcome.ToolFailedPaths.Add(path);
            return RvzDeepHashDisposition.ToolFailed;
        }
        var logical = verify.Hashes;
        Op("deep-hash", $"logical ISO SHA1 {logical.Sha1} · {header.LogicalIsoSize} bytes" +
                        (verify.ProblemsFound ? $" · DolphinTool reported {verify.Problems.Count} problem(s) (informational)" : ""));

        // ── 5. Match logical SHA1 against the DAT, confirming size/MD5/CRC32 where declared ──
        var accepted = new List<(string ReleaseId, ReleaseFileRecord File)>();
        if (needs.Sha1Index.TryGetValue(logical.Sha1, out var targets))
        {
            foreach (var (rid, rom) in targets)
            {
                var f = allReleaseFiles.TryGetValue(rid, out var rf) ? rf.FirstOrDefault(x => x.RomName == rom) : null;
                if (f is null) continue;
                var conflict = LogicalConflict(f, header.LogicalIsoSize, logical);
                if (conflict is not null)
                {
                    Op("rvz-logical-conflict", $"{(releases.TryGetValue(rid, out var cr) ? cr.Name : rid)}/{rom}: {conflict} — not matched");
                    continue;
                }
                accepted.Add((rid, f));
            }
        }

        if (accepted.Count == 0)
        {
            Op("rvz-no-match", $"logical ISO SHA1 {logical.Sha1} matches no DAT entry");
            MoveToSkip(s, path, "skip", "skip-failed", result, progress);
            result.FilesSkipped++;
            return RvzDeepHashDisposition.NoMatch;
        }

        var releaseIds = accepted.Select(a => a.ReleaseId).Distinct(StringComparer.Ordinal).ToList();
        if (releaseIds.Count > 1)
        {
            Op("rvz-ambiguous", $"logical ISO matches {releaseIds.Count} releases " +
               $"({string.Join(", ", releaseIds.Select(id => releases.TryGetValue(id, out var r) ? r.Name : id))}) — not assigned");
            MoveToSkip(s, path, "skip", "skip-failed", result, progress);
            result.FilesSkipped++;
            return RvzDeepHashDisposition.Ambiguous;
        }

        var release = releases[releaseIds[0]];
        var file    = accepted[0].File;

        if (release.Status == "unwanted")
        {
            Op("unwanted-classified", release.Name);
            MoveToSkip(s, path, "unwanted-moved", "unwanted-move-failed", result, progress);
            result.UnwantedSkipped++;
            return RvzDeepHashDisposition.Unwanted;
        }

        var sat = RedundantIncomingPolicy.Locate(release.Status, store.GetDerivedArtifactsByReleaseId(release.Id).Count);
        if (sat == ReleaseSatisfaction.Satisfied)
        {
            // Same rule as the ordinary Phase 8 `allTargetsSatisfied` branch: a uniquely, fully verified match for a
            // release the DB already holds as present-with-artifact is redundant → delete ONLY this incoming candidate.
            // The archived derived artifact is never read, moved or modified. No extra hashing pass is performed.
            Op("rvz-already-present", $"{release.Name} is already present (logical ISO SHA1 {logical.Sha1}) — redundant RVZ");
            result.FilesSkipped++;
            try
            {
                File.Delete(path);
                Op("duplicate-deleted", "incoming-roms");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Op("duplicate-delete-failed", $"duplicate source file could not be removed: {ex.Message}");
            }
            outcome.DuplicateReleaseByPath[path] = release.Id;
            return RvzDeepHashDisposition.AlreadySatisfied;
        }

        var releaseFiles = allReleaseFiles.TryGetValue(release.Id, out var all) ? all : new List<ReleaseFileRecord>();
        if (releaseFiles.Count != 1 || !s.IsRvzEligibleRom(file.RomName) ||
            DatFileRequirement.Classify(file) != DatFileRequirementKind.Physical)
        {
            Op("rvz-unsupported-release", $"{release.Name}: only single-file releases stored as RVZ can adopt an RVZ " +
                                          $"({releaseFiles.Count} file(s), {file.RomName})");
            MoveToSkip(s, path, "skip", "skip-failed", result, progress);
            result.FilesSkipped++;
            return RvzDeepHashDisposition.Unsupported;
        }

        return Adopt(s, path, name, store, release, file, header, phys, logical, outcome, result, progress);
    }

    /// <summary>Null when the DAT requirement agrees with the logical image on every declared attribute.</summary>
    internal static string? LogicalConflict(ReleaseFileRecord f, long logicalSize, RvzLogicalHashes logical)
    {
        if (long.TryParse(f.Size?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var datSize) &&
            datSize != logicalSize)
            return $"logical size {logicalSize} ≠ DAT size {datSize}";
        if (f.Md5.Length > 0 && logical.Md5.Length > 0 && !string.Equals(f.Md5, logical.Md5, StringComparison.OrdinalIgnoreCase))
            return $"logical MD5 {logical.Md5} ≠ DAT MD5 {f.Md5}";
        var crc = Crc32Hasher.Normalize(f.Crc);
        if (crc.Length > 0 && crc != logical.Crc32)
            return $"logical CRC32 {logical.Crc32} ≠ DAT CRC32 {crc}";
        return null;
    }

    private static RvzDeepHashDisposition Adopt(
        RvzDeepHashSettings          s,
        string                       srcPath,
        string                       name,
        DatLineStore                 store,
        ReleaseRecord                release,
        ReleaseFileRecord            f,
        RvzHeaderInfo                header,
        PhysicalHashes               phys,
        RvzLogicalHashes             logical,
        RvzDeepHashOutcome           outcome,
        IngestionResult              result,
        IProgress<IngestionProgress> progress)
    {
        void Op(string action, string detail) => Report(result, progress, new IngestionOperation(name, action, detail));
        RvzDeepHashDisposition Blocked(string action, string detail)
        {
            Op(action, $"{detail} — RVZ preserved in incoming");
            outcome.FailedReleaseIds.Add(release.Id);
            return RvzDeepHashDisposition.CommitBlocked;
        }

        var identity = FileCommitIdentityPolicy.Resolve(release.Id, f);
        if (!identity.CanCommit) return Blocked("file-commit-failed", identity.FailureReason);
        var ck = identity.ContentIdentityKey;

        // Idempotency: reuse the stored relative_path of an existing RVZ artifact for this content; an artifact of a
        // different storage strategy for the same content is an inconsistent state — never overwritten or re-pointed.
        var existing = store.GetDerivedByContentKey(ck);
        if (existing is not null && existing.StorageStrategyId != RvzIds.TransformId)
            return Blocked("rvz-existing-artifact-conflict",
                $"content already has a '{existing.StorageStrategyId}' artifact at {existing.RelativePath}");

        var safeFolder  = s.SafeFileName(release.Name);
        var newFileName = s.ArchiveForm == ArchiveDatLineOutputForm.SingleFileFlat
            ? safeFolder + RvzIds.OutputExtension
            : Path.GetFileNameWithoutExtension(f.RomName) + RvzIds.OutputExtension;
        var plan     = ArchiveWritePlanner.Plan(s.AppRoot, s.PlatformId, s.DatLineId, s.ArchiveForm, safeFolder, newFileName,
                                                existing?.RelativePath);
        var destPath = plan.FullPath;

        // Archive write collision guard (defense-in-depth): an existing target is NEVER overwritten.
        bool targetExists = File.Exists(destPath) || Directory.Exists(destPath);
        var decision = ArchiveWriteCollisionGuard.Decide(
            targetExists, store.GetDerivedArtifactContentKeysByRelativePath(plan.RelativePath), ck);
        if (ArchiveWriteCollisionGuard.IsBlocking(decision))
            return Blocked("archive-collision", $"target \"{plan.RelativePath}\" belongs to a different artifact ({decision}); not overwritten");
        if (targetExists)
            return Blocked("rvz-target-exists", $"target \"{plan.RelativePath}\" already exists for this content; not overwritten");

        var now = DateTime.UtcNow;
        try
        {
            // Content identity (DAT-declared) + logical source provenance (the verified ISO — no physical file implied).
            store.EnsureContentIdentity(new ContentIdentityRecord
            {
                ContentIdentityKey = ck,
                DatSha1            = f.Sha1.Length > 0 ? f.Sha1 : null,
                DatMd5             = f.Md5.Length  > 0 ? f.Md5  : null,
                DatCrc32           = f.Crc.Length  > 0 ? f.Crc  : null,
                CreatedAtUtc       = now,
            });
            var saId = Guid.NewGuid().ToString("N");
            store.SaveSourceArtifact(new SourceArtifactRecord
            {
                Id                 = saId,
                ContentIdentityKey = ck,
                SourceSizeBytes    = header.LogicalIsoSize,
                HashedSourceSha1   = logical.Sha1,
                HashedSourceMd5    = logical.Md5.Length > 0 ? logical.Md5 : null,   // never fabricated
                HashedSourceCrc32  = logical.Crc32,
                VerifiedAtUtc      = now,
            });
            saId = store.GetSourceArtifactIdByContentKey(ck) ?? saId;

            // ── Transfer: same filesystem = native move ONLY; across filesystems = copy (source removed after commit) ──
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            bool moved = s.SameFilesystem(srcPath, destPath);
            try
            {
                if (moved)
                {
                    File.Move(srcPath, destPath, overwrite: false);
                }
                else
                {
                    var free = TryFreeSpace(destPath);
                    if (free is long fr && fr < phys.Length)
                        return Blocked("rvz-transfer-failed", $"insufficient space on the archive drive ({fr} bytes free, {phys.Length} needed)");
                    s.CrossFilesystemCopy(srcPath, destPath);
                    if (new FileInfo(destPath).Length != phys.Length)
                    {
                        TryDelete(destPath);   // our own incomplete copy — the incoming original is untouched
                        return Blocked("rvz-transfer-failed", "size mismatch after cross-filesystem copy");
                    }
                    // A real second medium was written: verify the destination bytes against the physical SHA1 already
                    // computed for the candidate before the destination may be registered as the archived artifact.
                    var destSha1 = Sha1Of(destPath);
                    if (!string.Equals(destSha1, phys.Sha1, StringComparison.Ordinal))
                    {
                        TryDelete(destPath);   // our own unverified copy — never registered, incoming untouched
                        return Blocked("rvz-transfer-failed",
                            $"SHA1 mismatch after cross-filesystem copy (expected {phys.Sha1}, got {destSha1})");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!moved && File.Exists(srcPath)) TryDelete(destPath);   // partial copy of ours; never the original
                return Blocked("rvz-transfer-failed", ex.Message);
            }

            try
            {
                store.IngestDerivedArtifact(
                    contentIdentityKey: ck,
                    sourceArtifactId:   saId,
                    storageStrategyId:  RvzIds.TransformId,
                    fileName:           Path.GetFileName(destPath),
                    relativePath:       plan.RelativePath,
                    derivedSizeBytes:   phys.Length,
                    hashedDerivedSha1:  phys.Sha1,
                    hashedDerivedMd5:   phys.Md5,
                    hashedDerivedCrc32: phys.Crc32,
                    archiveTier:        "A");
                store.SaveReleaseContentLink(new ReleaseContentLinkRecord
                {
                    Id                 = Guid.NewGuid().ToString("N"),
                    ReleaseId          = release.Id,
                    ContentIdentityKey = ck,
                    CreatedAtUtc       = now,
                });
                store.UpdateReleaseStatus(release.Id, "present");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No archive file without its DB rows: undo the transfer (rename back / drop our copy).
                if (moved) { try { File.Move(destPath, srcPath, overwrite: false); } catch { } }
                else TryDelete(destPath);
                return Blocked("rvz-commit-failed", ex.Message);
            }

            Op(moved ? "rvz-adopted-moved" : "rvz-adopted-copied",
               $"{plan.RelativePath} (physical SHA1 {phys.Sha1}, {phys.Length} bytes; Tier A)");

            if (!moved)
            {
                try { File.Delete(srcPath); Op("delete", "incoming-roms"); }
                catch { Op("delete-failed", "source file could not be removed"); }
            }

            result.FilesMatched++;
            result.DerivedArtifactsCreated++;
            result.ReleasesPresent++;
            outcome.AdoptedReleaseByPath[srcPath] = release.Id;
            return RvzDeepHashDisposition.Adopted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Blocked("rvz-commit-failed", ex.Message);
        }
    }

    private static void MoveToSkip(
        RvzDeepHashSettings s, string srcPath, string action, string failAction,
        IngestionResult result, IProgress<IngestionProgress> progress)
    {
        var name = Path.GetFileName(srcPath);
        try
        {
            Directory.CreateDirectory(s.SkipDir);
            var dest = IngestionPaths.CollisionSafePath(s.SkipDir, name);
            File.Move(srcPath, dest, overwrite: false);
            Report(result, progress, new IngestionOperation(name, action, $"incoming-skip/{s.PlatformId}/{Path.GetFileName(dest)}"));
        }
        catch
        {
            Report(result, progress, new IngestionOperation(name, failAction, "could not move to incoming-skip"));
        }
    }

    private static string Sha1Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private static long? TryFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void Report(IngestionResult result, IProgress<IngestionProgress> progress, IngestionOperation op)
    {
        result.Operations.Add(op);
        progress.Report(new IngestionProgress { NewOperation = op });
    }
}

/// <summary>Physical digests + length of one file, computed in a SINGLE streamed read.</summary>
public sealed record PhysicalHashes(string Sha1, string Md5, string Crc32, long Length)
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static PhysicalHashes Compute(string path)
    {
        using var fs   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5  = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        uint crc  = 0xFFFFFFFF;
        long len  = 0;
        var  buf  = new byte[1 << 20];
        int  read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            sha1.AppendData(buf, 0, read);
            md5.AppendData(buf, 0, read);
            for (int i = 0; i < read; i++) crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            len += read;
        }
        return new PhysicalHashes(
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            (crc ^ 0xFFFFFFFF).ToString("x8"),
            len);
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
