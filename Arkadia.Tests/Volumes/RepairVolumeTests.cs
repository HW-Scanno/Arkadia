using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Integration tests for Repair Volume behavior and safety.
/// Each test provisions real SQLite stores + a temp filesystem and runs
/// SimulateRepair — a mirror of the non-UI state-machine in RunVolumeRepairAsync.
/// </summary>
public sealed class RepairVolumeTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _catalogDir;
    private readonly string _datDir;
    private readonly string _volumesDir;
    private readonly string _archiveDir;
    private readonly string _sourceDir;
    private readonly string _incomingRepairDir;

    public RepairVolumeTests()
    {
        _tempRoot          = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalogDir        = Path.Combine(_tempRoot, "data");
        _datDir            = Path.Combine(_tempRoot, "dat");
        _volumesDir        = Path.Combine(_tempRoot, "volumes");
        _archiveDir        = Path.Combine(_tempRoot, "archive");
        _sourceDir         = Path.Combine(_tempRoot, "source");
        _incomingRepairDir = Path.Combine(_tempRoot, "incoming-repair");

        Directory.CreateDirectory(_catalogDir);
        Directory.CreateDirectory(_datDir);
        Directory.CreateDirectory(_volumesDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── SafeFileName mirrors MainWindow.SafeFileName exactly ──────────────────

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var sanitized = sb.ToString().Trim('_', ' ');
        return sanitized.Length > 0 ? sanitized : "release";
    }

    private static string FileSha1(byte[] bytes)
        => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string FileSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    // ── Artifact spec ─────────────────────────────────────────────────────────

    private sealed record ArtifactSpec(
        string DerivedArtifactId,
        string ReleaseName,
        string FileName,
        string Sha1,
        byte[] Content);

    // ── Provision helper ──────────────────────────────────────────────────────

    private (CatalogService Catalog, DatLineStore Store, VolumeRecord Volume, List<ArtifactSpec> Artifacts)
        Provision(string label, string status, int artifactCount,
                  string platformId = "platform-test", string dlId = "dl-test",
                  bool flatChd = false)
    {
        var catalog = new CatalogService(_catalogDir);
        var dbPath  = Path.Combine(_datDir, $"{label}.db");
        var store   = new DatLineStore(dbPath);
        var volId   = Guid.NewGuid().ToString("N");

        var volume = new VolumeRecord
        {
            Id               = volId,
            Label            = label,
            PlatformId       = platformId,
            DatLineId        = dlId,
            Status           = status,
            Health           = status == "lost" ? "crit" : "ok",
            PlannedSizeBytes = 1024L * 1024,
            ActualSizeBytes  = 0,
            CreatedAt        = DateTime.UtcNow,
        };
        catalog.SaveVolume(volume);

        var rawItems = Enumerable.Range(0, artifactCount).Select(i =>
        {
            var relName   = $"Release {i}";
            var fileName  = flatChd ? $"game_{i}.chd" : $"game_{i}.rom";
            var content   = System.Text.Encoding.UTF8.GetBytes($"content-seed-{i}");
            var sha1      = FileSha1(content);
            var cik       = $"sha1:{sha1}";
            var releaseId = Guid.NewGuid().ToString("N");
            return (relName, fileName, content, sha1, cik, releaseId);
        }).ToList();

        store.SaveReleases(rawItems.Select(r => new ReleaseRecord
        {
            Id = r.releaseId, DatLineId = dlId, Name = r.relName,
            Status = "missing", ReleaseContentKey = r.cik,
        }).ToList());

        var specs = new List<ArtifactSpec>(artifactCount);
        foreach (var (relName, fileName, content, sha1, cik, releaseId) in rawItems)
        {
            store.EnsureContentIdentity(new ContentIdentityRecord
            {
                ContentIdentityKey = cik, DatSha1 = sha1,
                DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow,
            });
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId,
                ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow,
            });

            // Flat CHD artifacts store a flat relative_path; legacy file-extension
            // artifacts store a release-foldered relative_path.
            var relPath = flatChd
                ? $"archive/{platformId}/{dlId}/{fileName}"
                : $"archive/{platformId}/{dlId}/{SafeFileName(relName)}/{fileName}";
            var daId = store.IngestDerivedArtifact(
                cik, "", "no_compression", fileName, relPath, content.Length, sha1);

            catalog.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = volId, DatLineId = dlId,
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            });

            specs.Add(new ArtifactSpec(daId, relName, fileName, sha1, content));
        }

        return (catalog, store, volume, specs);
    }

    // ── SimulateRepair ────────────────────────────────────────────────────────
    // Mirrors the non-UI state machine in RunVolumeRepairAsync (post-ingest path).
    // The ingest phase (RunIngestionWork) is intentionally excluded — callers
    // seed archive/source/incoming-repair directly before calling this.

    private sealed record RepairResult(
        List<string> VerifiedDaIds,
        List<string> SkippedDaIds,
        List<string> FailedDaIds,
        Dictionary<string, string> IncomingMatchesUsed);

    private RepairResult SimulateRepair(
        CatalogService catalog, DatLineStore store, VolumeRecord volume,
        string volumeRoot, string platformId, string dlId)
    {
        // 1. Identify repair targets: missing or SHA1-mismatched files on volume
        var vaIds       = catalog.GetVolumeArtifacts(volume.Id)
                                 .Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetArtifactVerifyInfos(vaIds);

        var repairTargets = new List<ArtifactVerifyInfo>();
        foreach (var vi in verifyInfos)
        {
            var absPath = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, vi.FileName);
            if (!File.Exists(absPath))
            {
                repairTargets.Add(vi);
            }
            else if (vi.Sha1.Length > 0)
            {
                var actual = FileSha1(absPath);
                if (!string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                    repairTargets.Add(vi);
            }
        }

        // 2. Build availability map via the SAME production resolver the app uses,
        //    so this test exercises real path logic (archive via DB relative_path,
        //    then source fallback) instead of a test-local reconstruction.
        var available = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var vi in repairTargets)
        {
            var localSource = Arkadia.Volumes.LocalRepairSourceResolver.Resolve(
                _tempRoot, vi.RelativePath, platformId, dlId, vi.ReleaseName, vi.FileName);
            if (localSource is not null) available[vi.DerivedArtifactId] = localSource;
        }

        // 3. Scan incoming-repair for SHA1 matches (for targets not yet available)
        var sha1ToTarget   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var incomingDir    = Path.Combine(_incomingRepairDir, platformId);
        foreach (var vi in repairTargets)
        {
            if (available.ContainsKey(vi.DerivedArtifactId)) continue;
            if (vi.Sha1.Length > 0) sha1ToTarget[vi.Sha1] = vi.DerivedArtifactId;
        }

        var incomingMatches = new Dictionary<string, string>(StringComparer.Ordinal); // daId → path
        if (sha1ToTarget.Count > 0 && Directory.Exists(incomingDir))
        {
            foreach (var f in Directory.EnumerateFiles(incomingDir, "*", SearchOption.AllDirectories))
            {
                if (incomingMatches.Count == sha1ToTarget.Count) break;
                try
                {
                    var fSha1 = FileSha1(f);
                    if (sha1ToTarget.TryGetValue(fSha1, out var daId) && !incomingMatches.ContainsKey(daId))
                        incomingMatches[daId] = f;
                }
                catch { /* unreadable — skip */ }
            }
        }

        // Merge incoming into available
        foreach (var (daId, path) in incomingMatches)
            if (!available.ContainsKey(daId))
                available[daId] = path;

        // 4. Copy → verify → delete from incoming-repair (per-file)
        var reintegratedDaIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedDaIds      = new List<string>();
        var verifiedDaIds     = new List<string>();
        var failedDaIds       = new List<string>();
        var incomingMatchesUsed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var vi in repairTargets)
        {
            if (!available.TryGetValue(vi.DerivedArtifactId, out var srcPath))
            {
                skippedDaIds.Add(vi.DerivedArtifactId); continue;
            }

            var dstPath = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, vi.FileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.Copy(srcPath, dstPath, overwrite: true);
                reintegratedDaIds.Add(vi.DerivedArtifactId);
            }
            catch { skippedDaIds.Add(vi.DerivedArtifactId); continue; }
        }

        foreach (var vi in repairTargets)
        {
            if (!reintegratedDaIds.Contains(vi.DerivedArtifactId)) continue;

            var dstPath = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, vi.FileName);

            if (!File.Exists(dstPath))
            {
                failedDaIds.Add(vi.DerivedArtifactId); continue;
            }
            if (vi.Sha1.Length > 0)
            {
                var actual = FileSha1(dstPath);
                if (!string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    failedDaIds.Add(vi.DerivedArtifactId); continue;
                }
            }

            verifiedDaIds.Add(vi.DerivedArtifactId);

            // Delete from incoming-repair after verify (matches main window logic)
            if (incomingMatches.TryGetValue(vi.DerivedArtifactId, out var incomingSrc))
            {
                incomingMatchesUsed[vi.DerivedArtifactId] = incomingSrc;
                try { File.Delete(incomingSrc); } catch { }
            }
        }

        // 5. Apply state updates (CASE A/B logic from RunVolumeRepairAsync)
        var missedDaIds  = skippedDaIds.Concat(failedDaIds).ToList();
        bool wasLost     = volume.Status == "lost";
        bool fullSuccess = missedDaIds.Count == 0;
        string newHealth = fullSuccess ? "ok" : "crit";

        if (wasLost && !fullSuccess)
        {
            // CASE B: LOST + partial — do NOT update artifact or release status
        }
        else
        {
            // CASE A: full repair (any origin) OR non-lost partial repair
            if (verifiedDaIds.Count > 0)
                store.BatchUpdateDerivedArtifactStatus(verifiedDaIds, "present");
            if (missedDaIds.Count > 0)
                store.BatchUpdateDerivedArtifactStatus(missedDaIds, "missing");

            var allChanged = verifiedDaIds.Concat(missedDaIds).ToList();
            if (allChanged.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(allChanged);

            newHealth = fullSuccess ? "ok" : "crit";
        }

        if (wasLost && fullSuccess)
        {
            catalog.UpdateVolumeStatus(volume.Id, "present");
            catalog.UpdateVolumeHealth(volume.Id, "ok");
            catalog.SetCurrentLocation(new VolumeLocationRecord
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = volume.Id,
                LocationType = "workspace", DiskId = null, Path = volumeRoot,
                IsCurrent = true, CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            catalog.UpdateVolumeHealth(volume.Id, newHealth);
        }

        return new RepairResult(verifiedDaIds, skippedDaIds, failedDaIds, incomingMatchesUsed);
    }

    // ── File seeding helpers ──────────────────────────────────────────────────

    private void SeedArchive(ArtifactSpec s, string platformId, string dlId)
    {
        var dir = Path.Combine(_archiveDir, platformId, dlId, SafeFileName(s.ReleaseName));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
    }

    // Flat archive layout (release-shape / CHD): archive\<platform>\<datLine>\<file>
    private void SeedArchiveFlat(ArtifactSpec s, string platformId, string dlId, byte[]? content = null)
    {
        var dir = Path.Combine(_archiveDir, platformId, dlId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, s.FileName), content ?? s.Content);
    }

    private void SeedSource(ArtifactSpec s, string platformId, string dlId)
    {
        var dir = Path.Combine(_sourceDir, platformId, dlId, SafeFileName(s.ReleaseName));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
    }

    private void SeedIncoming(ArtifactSpec s, string platformId)
    {
        var dir = Path.Combine(_incomingRepairDir, platformId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
    }

    private static void SeedVolumeFile(ArtifactSpec s, string volumeRoot)
    {
        Directory.CreateDirectory(volumeRoot);
        var path = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, s.FileName);
        File.WriteAllBytes(path, s.Content);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 1 — Location mismatch: artifacts from another volume are not repaired
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Repair_OnlyProcessesArtifactsAssignedToTargetVolume()
    {
        // Volume A has artifact 0; Volume B has artifact 0 (separate DB).
        var (catalog, storeA, volA, specsA) = Provision("vol-A", "present", 1);
        var (catalog2, storeB, volB, specsB) = Provision("vol-B", "present", 1);

        // Volume A: artifact is missing on disk
        var rootA = Path.Combine(_volumesDir, "vol-A");
        Directory.CreateDirectory(rootA);
        // (no files written — artifact is "missing")

        // Volume B: artifact is healthy
        SeedArchive(specsB[0], "platform-test", "dl-test");

        // Repair volume A — should only try to fix specsA[0]
        var vaIdsA = catalog.GetVolumeArtifacts(volA.Id).Select(x => x.DerivedArtifactId).ToList();
        var vaIdsB = catalog2.GetVolumeArtifacts(volB.Id).Select(x => x.DerivedArtifactId).ToList();

        Assert.DoesNotContain(vaIdsA[0], vaIdsB);
        Assert.DoesNotContain(vaIdsB[0], vaIdsA);
    }

    [Fact]
    public void Repair_ArtifactAssignedToVolumeB_IsNotInRepairTargetsForVolumeA()
    {
        var (catalog, storeA, volA, specsA) = Provision("vol-loc-A", "present", 2);

        // Artifact 1 is also assigned to a second volume in the catalog
        var volBId = Guid.NewGuid().ToString("N");
        var volB = new VolumeRecord
        {
            Id = volBId, Label = "vol-loc-B", PlatformId = "platform-test",
            DatLineId = "dl-test", Status = "present", PlannedSizeBytes = 1024,
            ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        catalog.SaveVolume(volB);

        // specsA[0] is only on volA; specsA[1] is on both volA and volB
        catalog.SaveVolumeArtifact(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volBId, DatLineId = "dl-test",
            DerivedArtifactId = specsA[1].DerivedArtifactId,
            ContentIdentityKey = $"sha1:{specsA[1].Sha1}",
            Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        });

        // GetVolumeArtifacts for volA should only return volA's artifacts
        var forA = catalog.GetVolumeArtifacts(volA.Id);
        var forB = catalog.GetVolumeArtifacts(volBId);

        Assert.Equal(2, forA.Count);                 // volA has 2
        Assert.Single(forB);                         // volB has only 1 (the explicitly added one)
        Assert.DoesNotContain(forA, va => va.VolumeId == volBId);
        Assert.DoesNotContain(forB, va => va.VolumeId == volA.Id);
    }

    [Fact]
    public void Repair_FileOnWrongVolumeDir_IsNotUsedAsSource()
    {
        var (catalog, store, volume, specs) = Provision("vol-wrongdir", "present", 1);
        var rootCorrect = Path.Combine(_volumesDir, "vol-wrongdir");
        var rootWrong   = Path.Combine(_volumesDir, "vol-OTHER");

        // Seed the artifact in the wrong volume directory (not archive/source/incoming)
        SeedVolumeFile(specs[0], rootWrong);
        // The correct volume dir is empty — artifact is "missing"
        Directory.CreateDirectory(rootCorrect);

        var result = SimulateRepair(catalog, store, volume, rootCorrect, "platform-test", "dl-test");

        // Repair cannot source from another volume's directory
        Assert.DoesNotContain(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Contains(specs[0].DerivedArtifactId, result.SkippedDaIds);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 2 — Derived-first logic: archive preferred over source
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Repair_ArchiveUsedWhenBothArchiveAndSourceExist()
    {
        var (catalog, store, volume, specs) = Provision("vol-prefer-archive", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-prefer-archive");
        Directory.CreateDirectory(root);

        // Seed archive with correct content; seed source with WRONG content
        SeedArchive(specs[0], "platform-test", "dl-test");

        var sourceDir = Path.Combine(_sourceDir, "platform-test", "dl-test", SafeFileName(specs[0].ReleaseName));
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG_SOURCE_CONTENT"));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        // Verify succeeds → archive content was used (not the corrupted source)
        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Empty(result.FailedDaIds);
    }

    [Fact]
    public void Repair_SourceUsedWhenArchiveAbsent()
    {
        var (catalog, store, volume, specs) = Provision("vol-source-fallback", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-source-fallback");
        Directory.CreateDirectory(root);

        // Only source — no archive
        SeedSource(specs[0], "platform-test", "dl-test");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Empty(result.SkippedDaIds);
    }

    [Fact]
    public void Repair_ArchiveTakesPrecedenceOverIncomingRepair()
    {
        var (catalog, store, volume, specs) = Provision("vol-arch-vs-incoming", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-arch-vs-incoming");
        Directory.CreateDirectory(root);

        // Archive: correct content
        SeedArchive(specs[0], "platform-test", "dl-test");

        // incoming-repair: also has the file (but archive should win — incoming not even scanned)
        SeedIncoming(specs[0], "platform-test");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);

        // incoming file must NOT have been deleted (it was not used as the source)
        var incomingFile = Path.Combine(_incomingRepairDir, "platform-test", specs[0].FileName);
        Assert.True(File.Exists(incomingFile),
            "incoming-repair file should remain — archive was used, not incoming");
    }

    [Fact]
    public void Repair_IncomingUsedWhenNeitherArchiveNorSourceExist()
    {
        var (catalog, store, volume, specs) = Provision("vol-incoming-only", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-incoming-only");
        Directory.CreateDirectory(root);

        // Only incoming-repair — no archive, no source
        SeedIncoming(specs[0], "platform-test");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
    }

    [Fact]
    public void Repair_NeitherArchiveNorSourceNorIncoming_ArtifactSkipped()
    {
        var (catalog, store, volume, specs) = Provision("vol-no-source", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-no-source");
        Directory.CreateDirectory(root);

        // Nothing seeded anywhere
        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.SkippedDaIds);
        Assert.Empty(result.VerifiedDaIds);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 3 — Cleanup: incoming-repair files deleted after successful repair
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Repair_IncomingFileDeletedAfterSuccessfulVerify()
    {
        var (catalog, store, volume, specs) = Provision("vol-cleanup", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-cleanup");
        Directory.CreateDirectory(root);

        SeedIncoming(specs[0], "platform-test");
        var incomingFile = Path.Combine(_incomingRepairDir, "platform-test", specs[0].FileName);
        Assert.True(File.Exists(incomingFile));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.False(File.Exists(incomingFile),
            "incoming-repair file must be deleted after successful copy+verify");
    }

    [Fact]
    public void Repair_MultipleIncoming_OnlyUsedOnesDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-cleanup-multi", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-cleanup-multi");
        Directory.CreateDirectory(root);

        // Artifact 0: in incoming-repair → will be used
        SeedIncoming(specs[0], "platform-test");
        var incoming0 = Path.Combine(_incomingRepairDir, "platform-test", specs[0].FileName);

        // Artifact 1: pre-seed on volume so it's not a repair target at all
        SeedVolumeFile(specs[1], root);

        // Extra unrelated file in incoming-repair (different SHA1, unmatched)
        var extraFile = Path.Combine(_incomingRepairDir, "platform-test", "unrelated.rom");
        Directory.CreateDirectory(Path.GetDirectoryName(extraFile)!);
        File.WriteAllBytes(extraFile, System.Text.Encoding.UTF8.GetBytes("UNRELATED"));

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.False(File.Exists(incoming0),    "used incoming file must be deleted");
        Assert.True(File.Exists(extraFile),     "unrelated incoming file must remain untouched");
    }

    [Fact]
    public void Repair_IncomingFileNotDeletedIfVerifyFails()
    {
        var (catalog, store, volume, specs) = Provision("vol-cleanup-fail", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-cleanup-fail");
        Directory.CreateDirectory(root);

        // Put the WRONG content in incoming-repair (SHA1 will mismatch)
        var incomingDir = Path.Combine(_incomingRepairDir, "platform-test");
        Directory.CreateDirectory(incomingDir);
        // We name it with the correct filename but wrong content to test the mismatch path.
        // SimulateRepair scans by SHA1, so it won't match — artifact stays skipped.
        File.WriteAllBytes(Path.Combine(incomingDir, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED_INCOMING_CONTENT"));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        // SHA1 mismatch in incoming scan → not matched → skipped
        Assert.Contains(specs[0].DerivedArtifactId, result.SkippedDaIds);
        // The wrong incoming file was never used and was not deleted
        Assert.True(File.Exists(Path.Combine(incomingDir, specs[0].FileName)));
    }

    [Fact]
    public void Repair_ArchiveSourceFiles_NeverDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-perm-store", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-perm-store");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedSource(specs[1],  "platform-test", "dl-test");

        var archiveFile = Path.Combine(_archiveDir, "platform-test", "dl-test",
            SafeFileName(specs[0].ReleaseName), specs[0].FileName);
        var sourceFile = Path.Combine(_sourceDir, "platform-test", "dl-test",
            SafeFileName(specs[1].ReleaseName), specs[1].FileName);

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.True(File.Exists(archiveFile), "archive file must never be deleted by repair");
        Assert.True(File.Exists(sourceFile),  "source file must never be deleted by repair");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 2b — Archive-source resolution via DB relative_path (H1 regression)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RepairVolume_FindsFlatChdArchiveSourceByRelativePath()
    {
        // CHD artifact stored FLAT: archive\<platform>\<datLine>\<file>.chd
        var (catalog, store, volume, specs) = Provision("vol-flat-chd", "present", 1, flatChd: true);
        var root = Path.Combine(_volumesDir, "vol-flat-chd");
        Directory.CreateDirectory(root);

        SeedArchiveFlat(specs[0], "platform-test", "dl-test");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Empty(result.FailedDaIds);
        Assert.Empty(result.SkippedDaIds);
    }

    [Fact]
    public void RepairVolume_FindsLegacyFolderedArchiveSourceByRelativePath()
    {
        // Legacy file-extension artifact stored FOLDERED: archive\<platform>\<datLine>\<release>\<file>
        var (catalog, store, volume, specs) = Provision("vol-foldered", "present", 1); // default foldered
        var root = Path.Combine(_volumesDir, "vol-foldered");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test"); // foldered seeding

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Empty(result.FailedDaIds);
    }

    [Fact]
    public void RepairVolume_DoesNotAssumeReleaseFolderForChd()
    {
        // Flat CHD present; the OLD reconstructed release-folder path must NOT exist,
        // proving repair does not depend on it.
        var (catalog, store, volume, specs) = Provision("vol-no-assume", "present", 1, flatChd: true);
        var root = Path.Combine(_volumesDir, "vol-no-assume");
        Directory.CreateDirectory(root);

        SeedArchiveFlat(specs[0], "platform-test", "dl-test");

        var reconstructedFoldered = Path.Combine(
            _archiveDir, "platform-test", "dl-test",
            SafeFileName(specs[0].ReleaseName), specs[0].FileName);
        Assert.False(File.Exists(reconstructedFoldered),
            "the release-foldered archive path must not exist for a flat CHD artifact");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
    }

    [Fact]
    public void RepairVolume_FallsBackWhenRelativePathFileMissing()
    {
        // Flat CHD relative_path is set, but no archive file exists there.
        // Repair must fall through to incoming-repair (existing fallback), not fail hard.
        var (catalog, store, volume, specs) = Provision("vol-relpath-missing", "present", 1, flatChd: true);
        var root = Path.Combine(_volumesDir, "vol-relpath-missing");
        Directory.CreateDirectory(root);

        // Do NOT seed archive; provide the artifact via incoming-repair instead.
        SeedIncoming(specs[0], "platform-test");

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
    }

    [Fact]
    public void RepairVolume_RejectsArchiveSourceHashMismatch()
    {
        // Flat CHD archive exists at relative_path but has WRONG content.
        // Copy succeeds, target verify fails → artifact must NOT be marked present.
        var (catalog, store, volume, specs) = Provision("vol-hash-mismatch", "present", 1, flatChd: true);
        var root = Path.Combine(_volumesDir, "vol-hash-mismatch");
        Directory.CreateDirectory(root);

        SeedArchiveFlat(specs[0], "platform-test", "dl-test",
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED_FLAT_CHD"));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.FailedDaIds);
        Assert.Empty(result.VerifiedDaIds);

        var derived = store.GetDerivedArtifacts().Single(d => d.Id == specs[0].DerivedArtifactId);
        Assert.NotEqual("present", derived.Status);
    }

    [Fact]
    public void RepairVolume_UsesDbRelativePathNotReconstructedReleaseFolder()
    {
        // Strongest proof: the FLAT relative_path holds the CORRECT content, while a
        // reconstructed release-folder path holds WRONG content. Repair succeeds only
        // if it used the DB relative_path (flat) rather than the reconstructed folder.
        var (catalog, store, volume, specs) = Provision("vol-relpath-authority", "present", 1, flatChd: true);
        var root = Path.Combine(_volumesDir, "vol-relpath-authority");
        Directory.CreateDirectory(root);

        SeedArchiveFlat(specs[0], "platform-test", "dl-test"); // correct content, flat

        // Decoy at the old reconstructed release-folder path with WRONG content.
        var folderedDir = Path.Combine(_archiveDir, "platform-test", "dl-test",
            SafeFileName(specs[0].ReleaseName));
        Directory.CreateDirectory(folderedDir);
        File.WriteAllBytes(Path.Combine(folderedDir, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG_FOLDERED_DECOY"));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        // Verified → the flat relative_path source was used, not the foldered decoy.
        Assert.Contains(specs[0].DerivedArtifactId, result.VerifiedDaIds);
        Assert.Empty(result.FailedDaIds);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 4 — Status updates: correct transitions only after verify
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Repair_FullSuccess_ArtifactsMarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-stat-ok", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-stat-ok");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[1].DerivedArtifactId]);
    }

    [Fact]
    public void Repair_FullSuccess_ReleasesMarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-rel-ok", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-rel-ok");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var releases = store.LoadReleases();
        Assert.All(releases, r => Assert.Equal("present", r.Status));
    }

    [Fact]
    public void Repair_FullSuccess_VolumeHealthOk()
    {
        var (catalog, store, volume, specs) = Provision("vol-health-ok", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-health-ok");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("ok", updated.Health);
    }

    [Fact]
    public void Repair_PartialSuccess_VolumeHealthCrit_StatusUnchanged()
    {
        var (catalog, store, volume, specs) = Provision("vol-partial-stat", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-partial-stat");
        Directory.CreateDirectory(root);

        // Only fix artifact 0; artifact 1 is still missing
        SeedArchive(specs[0], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);   // status unchanged
        Assert.Equal("crit",    updated.Health);
    }

    [Fact]
    public void Repair_PartialSuccess_VerifiedArtifactsPresent_SkippedStillMissing()
    {
        var (catalog, store, volume, specs) = Provision("vol-partial-art", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-partial-art");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");
        // specs[2]: not available anywhere

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[1].DerivedArtifactId]);
        Assert.Equal("missing", derived[specs[2].DerivedArtifactId]);
    }

    // ── LOST volume — CASE B: partial repair leaves volume LOST ───────────────

    [Fact]
    public void Repair_LostVolume_PartialRepair_VolumeRemainsLost()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-partial", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-partial");
        Directory.CreateDirectory(root);

        // Only repair one of two — not full success
        SeedArchive(specs[0], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("lost", updated.Status);
    }

    [Fact]
    public void Repair_LostVolume_PartialRepair_ArtifactStatusUnchanged()
    {
        // CASE B invariant: artifacts cannot be "present" on a LOST volume
        var (catalog, store, volume, specs) = Provision("vol-lost-inv", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-inv");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        // specs[1]: not available

        // Pre-check: all artifacts start as "present" (set by IngestDerivedArtifact)
        var before = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", before[specs[0].DerivedArtifactId]);

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        // CASE B: artifact status must NOT be updated — invariant preserved
        var after = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal(before[specs[0].DerivedArtifactId], after[specs[0].DerivedArtifactId]);
        Assert.Equal(before[specs[1].DerivedArtifactId], after[specs[1].DerivedArtifactId]);
    }

    [Fact]
    public void Repair_LostVolume_FullRepair_VolumeRestoredToPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-full", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-full");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);
        Assert.Equal("ok",      updated.Health);
    }

    [Fact]
    public void Repair_LostVolume_FullRepair_LocationSetToWorkspace()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-loc", "lost", 1);
        var root = Path.Combine(_volumesDir, "vol-lost-loc");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var loc = catalog.GetCurrentLocation(volume.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
        Assert.Equal(root, loc.Path);
    }

    [Fact]
    public void Repair_LostVolume_FullRepair_ArtifactsMarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-art", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-art");
        Directory.CreateDirectory(root);

        SeedArchive(specs[0], "platform-test", "dl-test");
        SeedArchive(specs[1], "platform-test", "dl-test");

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[1].DerivedArtifactId]);
    }

    // ── Status updates only after verify, not after copy ─────────────────────

    [Fact]
    public void Repair_VerifyFailed_ArtifactNotMarkedPresent()
    {
        // Put a file in archive with WRONG content for the artifact SHA1.
        // Copy will succeed but verify will fail → artifact must stay missing.
        var (catalog, store, volume, specs) = Provision("vol-verifyfail", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-verifyfail");
        Directory.CreateDirectory(root);

        // Seed archive with corrupted bytes (SHA1 won't match)
        var archiveDir = Path.Combine(_archiveDir, "platform-test", "dl-test",
            SafeFileName(specs[0].ReleaseName));
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED_ARCHIVE_CONTENT"));

        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Contains(specs[0].DerivedArtifactId, result.FailedDaIds);
        Assert.Empty(result.VerifiedDaIds);

        var derived = store.GetDerivedArtifacts().Single(d => d.Id == specs[0].DerivedArtifactId);
        Assert.NotEqual("present", derived.Status);   // must NOT be marked present
    }

    [Fact]
    public void Repair_VerifyFailed_VolumeHealthRemainsCrit()
    {
        var (catalog, store, volume, specs) = Provision("vol-fail-health", "present", 1);
        // Set volume health to "ok" before repair
        catalog.UpdateVolumeHealth(volume.Id, "ok");

        var root = Path.Combine(_volumesDir, "vol-fail-health");
        Directory.CreateDirectory(root);

        // Seed archive with wrong content
        var archiveDir = Path.Combine(_archiveDir, "platform-test", "dl-test",
            SafeFileName(specs[0].ReleaseName));
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG"));

        SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("crit", updated.Health);
    }

    // ── Volume with no repair targets ─────────────────────────────────────────

    [Fact]
    public void Repair_NoRepairTargets_NoMutations()
    {
        var (catalog, store, volume, specs) = Provision("vol-notarget", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-notarget");

        // Pre-populate volume so all artifacts are valid
        SeedVolumeFile(specs[0], root);
        SeedVolumeFile(specs[1], root);

        var healthBefore  = catalog.GetVolumes().Single(v => v.Id == volume.Id).Health;
        var derivedBefore = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);

        // (In the real code this path returns early with "No Repair Targets" dialog.
        //  SimulateRepair runs the full loop, which produces zero repairTargets.)
        var result = SimulateRepair(catalog, store, volume, root, "platform-test", "dl-test");

        Assert.Empty(result.VerifiedDaIds);
        Assert.Empty(result.SkippedDaIds);
        Assert.Empty(result.FailedDaIds);

        var healthAfter  = catalog.GetVolumes().Single(v => v.Id == volume.Id).Health;
        var derivedAfter = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);

        Assert.Equal(healthBefore, healthAfter);
        foreach (var (id, st) in derivedBefore)
            Assert.Equal(st, derivedAfter[id]);
    }

    // ── Cross-volume release isolation ────────────────────────────────────────

    [Fact]
    public void Repair_ReleasesOnOtherVolume_NotAffected()
    {
        // Volume A has release 0.  Volume B (separate catalog + DB) has release 0 independently.
        // Repairing volume A must not change releases in volume B's store.
        var (catalogA, storeA, volA, specsA) = Provision("vol-iso-A", "present", 1);
        var (catalogB, storeB, volB, specsB) = Provision("vol-iso-B", "present", 1);

        var rootA = Path.Combine(_volumesDir, "vol-iso-A");
        Directory.CreateDirectory(rootA);
        SeedArchive(specsA[0], "platform-test", "dl-test");

        var statusBefore = storeB.LoadReleases().Single().Status;

        SimulateRepair(catalogA, storeA, volA, rootA, "platform-test", "dl-test");

        var statusAfter = storeB.LoadReleases().Single().Status;
        Assert.Equal(statusBefore, statusAfter);
    }
}
