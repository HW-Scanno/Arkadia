using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Data.Identifiers;

namespace Arkadia.GroupDats;

/// <summary>
/// UI-free executor that turns a frozen <see cref="GroupDatReconciliationPlan"/> in
/// <see cref="GroupDatReconciliationMode.NewGroup"/> into a really-persisted Group DAT. It depends only on
/// <see cref="CatalogService"/> and the runtime <c>dataDir</c> — never on MainWindow/Avalonia/dialogs/the
/// selected System or DAT.
///
/// <para>Execution order (all-or-nothing; the catalog becomes visible only in the last phase):
/// global revalidation of every leaf → prepare all leaf DBs in per-target temp files → verify-all barrier →
/// publish (rename) every temp to its final path → one atomic <see cref="CatalogService.CreateDatGroupWithLeaves"/>
/// commit. Any failure before the commit leaves the catalog unchanged and cleans up only THIS execution's
/// files. A catalog failure after publish rolls the catalog back (M2 guarantee) and the executor then
/// attempts to remove only the final DBs it created.</para>
///
/// <para>Reuses <see cref="LeafDatDatabaseBuilder"/> for all leaf-DB mapping/building/verification and
/// <see cref="CatalogService.CreateDatGroupWithLeaves"/> for the atomic catalog registration — it duplicates
/// none of that logic. v1 is strictly sequential and deterministic; it spawns no threads for leaves.</para>
/// </summary>
public sealed class GroupDatExecutionService
{
    private readonly CatalogService _catalog;
    private readonly string         _dataDir;

    // Test-only seams: internal (not public API), instance-scoped (no shared static state), invoked only when
    // a test explicitly sets them, and with no production behaviour depending on them.
    internal Action<int>?        OnLeafPreparedForTests;    // after each leaf's temp DB is built+verified (1-based index)
    internal Action<int>?        OnLeafPublishedForTests;   // after each temp→final rename (1-based index)
    internal Func<string, bool>? TryDeleteOverrideForTests; // replaces File.Delete in cleanup; return false to force a cleanup failure

    public GroupDatExecutionService(CatalogService catalog, string dataDir)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dataDir = string.IsNullOrWhiteSpace(dataDir)
            ? throw new ArgumentException("dataDir is required.", nameof(dataDir))
            : dataDir;
    }

    /// <summary>
    /// Executes a frozen Create plan. All outcomes — including validation/stale, cancellation, and cleanup
    /// state — are reported through <see cref="GroupDatExecutionResult"/> rather than by throwing for control
    /// flow. Orchestration is synchronous under an async signature because every I/O here (SQLite via
    /// <see cref="DatLineStore"/>, <see cref="File.Move(string,string)"/>, SHA-256) is synchronous and v1 is
    /// intentionally sequential; no arbitrary <c>Task.Run</c> / worker threads are spun up.
    /// </summary>
    public Task<GroupDatExecutionResult> ExecuteCreateAsync(
        GroupDatReconciliationPlan            plan,
        IProgress<GroupDatExecutionProgress>? progress,
        CancellationToken                     cancellationToken)
        => Task.FromResult(ExecuteCreate(plan, progress, cancellationToken));

    // ---------------------------------------------------------------------------------------------------

    private GroupDatExecutionResult ExecuteCreate(
        GroupDatReconciliationPlan            plan,
        IProgress<GroupDatExecutionProgress>? progress,
        CancellationToken                     ct)
    {
        // ---- Part 2: structural plan preconditions (no filesystem/catalog mutation) ----
        var structuralError = ValidatePlanStructure(plan);
        if (structuralError is not null) return structuralError;

        string groupId = plan.GroupId;
        int    total   = plan.NewLeaves.Length;
        string execId  = Guid.NewGuid().ToString("N");

        // Build leaf execution contexts + detect intra-plan collisions before touching anything.
        var leaves      = new List<LeafExec>(total);
        var leafIdSeen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalRelSeen= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapByRel   = plan.DiscoverySnapshot
            .Where(l => l.ParseSucceeded)
            .ToDictionary(l => l.RelativePath, l => l, StringComparer.Ordinal);

        foreach (var nl in plan.NewLeaves)
        {
            if (!DatTechnicalIdPolicy.IsValidNew(nl.LeafId))
                return Abort(groupId, total, GroupDatExecutionErrorCode.InvalidPlan, $"Leaf id '{nl.LeafId}' is not a valid new id.", nl.LeafId);
            if (!leafIdSeen.Add(nl.LeafId))
                return Abort(groupId, total, GroupDatExecutionErrorCode.InvalidPlan, "Duplicate leaf id in the plan.", nl.LeafId);
            if (!snapByRel.TryGetValue(nl.SourceRelativePath, out var snap))
                return Abort(groupId, total, GroupDatExecutionErrorCode.InvalidPlan, "A new leaf has no matching discovered DAT.", nl.LeafId);

            var finalRel = $"systems/{plan.SystemId}/{nl.LeafId}.db";     // same canonical convention as Single-DAT Import
            if (!finalRelSeen.Add(finalRel))
                return Abort(groupId, total, GroupDatExecutionErrorCode.InvalidPlan, "Two leaves resolve to the same DataStorePath.", nl.LeafId);

            var finalAbs = Path.Combine(_dataDir, "systems", plan.SystemId, nl.LeafId + ".db");
            leaves.Add(new LeafExec
            {
                Plan         = nl,
                Snapshot     = snap,
                FinalRelPath = finalRel,
                FinalAbsPath = finalAbs,
                TempAbsPath  = finalAbs + ".tmp-" + execId,   // beside the final, same directory / filesystem
            });
        }

        // Every parsed discovered DAT must be represented by the Create plan (none silently dropped).
        var plannedRelPaths = new HashSet<string>(plan.NewLeaves.Select(nl => nl.SourceRelativePath), StringComparer.Ordinal);
        foreach (var l in plan.DiscoverySnapshot.Where(l => l.ParseSucceeded))
            if (!plannedRelPaths.Contains(l.RelativePath))
                return Abort(groupId, total, GroupDatExecutionErrorCode.InvalidPlan, "A discovered DAT is not represented in the Create plan.");

        var manifest       = new ExecManifest();
        string? currentLeaf = null;

        try
        {
            // ---- Parts 3+4+6: GLOBAL revalidation of ALL leaves, before the first temp DB ----
            ct.ThrowIfCancellationRequested();

            if (_catalog.DatGroupExists(DatGroupId.FromPersisted(groupId)))
                return Abort(groupId, total, GroupDatExecutionErrorCode.GroupIdCollision, $"Group id '{groupId}' already exists.");
            if (_catalog.GetHardwareFamily(plan.HardwareFamilyId) is null)
                return Abort(groupId, total, GroupDatExecutionErrorCode.HardwareFamilyMissing, "The System / hardware family no longer exists.");

            var existing    = _catalog.LoadDatLines();
            var existingIds = new HashSet<string>(existing.Select(d => d.Id),            StringComparer.OrdinalIgnoreCase);
            var existingDsp = new HashSet<string>(existing.Select(d => d.DataStorePath), StringComparer.OrdinalIgnoreCase);
            var mediaTypes  = new HashSet<string>(_catalog.GetMediaTypes().Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            var root = Path.GetFullPath(plan.SourceRoot);
            if (!Directory.Exists(root))
                return Abort(groupId, total, GroupDatExecutionErrorCode.SourceRootMissing, "The source root is not available.");

            int idx = 0;
            foreach (var lf in leaves)
            {
                idx++;
                ct.ThrowIfCancellationRequested();
                progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.Revalidating, idx, total, lf.Plan.LeafId, $"Revalidating DAT {idx} / {total}"));

                // Catalog revalidation (preflight; CreateDatGroupWithLeaves re-checks all of this in its transaction).
                if (existingIds.Contains(lf.Plan.LeafId))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.LeafIdCollision, $"Leaf id '{lf.Plan.LeafId}' already exists.", lf.Plan.LeafId);
                if (!mediaTypes.Contains(lf.Plan.MediaTypeId))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.MediaTypeMissing, $"Media type '{lf.Plan.MediaTypeId}' no longer exists.", lf.Plan.LeafId);
                if (existingDsp.Contains(lf.FinalRelPath))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.DataStorePathCollision, "A dat_line already uses this leaf's DataStorePath.", lf.Plan.LeafId);

                // Part 6: pre-existing target protection — never overwrite / never delete runtime data.
                if (File.Exists(lf.FinalAbsPath))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.DestinationOccupied,
                        "A leaf database already exists at the target path without a matching catalog dat_line; resolve it manually.", lf.Plan.LeafId);
                if (File.Exists(lf.TempAbsPath))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.DestinationOccupied,
                        "A temp database for this execution already exists at the target.", lf.Plan.LeafId);

                // Part 3: reconstruct + validate the source path (do NOT trust plan.SourcePath as identity).
                var srcAbs = ValidateSourcePath(root, lf.Plan.SourceRelativePath, out var srcMsg, out var srcCode);
                if (srcAbs is null)
                    return Abort(groupId, total, srcCode, srcMsg, lf.Plan.LeafId);

                // Reparse and compare deterministically against the frozen snapshot (staleness check).
                DatParser.Result parsed;
                try { parsed = DatParser.Parse(srcAbs); }
                catch { return Abort(groupId, total, GroupDatExecutionErrorCode.ReparseFailed, "The source DAT could not be parsed.", lf.Plan.LeafId); }
                if (!parsed.Success)
                    return Abort(groupId, total, GroupDatExecutionErrorCode.ReparseFailed, "The source DAT could not be parsed.", lf.Plan.LeafId);
                if (!SnapshotMatches(lf.Snapshot, parsed))
                    return Abort(groupId, total, GroupDatExecutionErrorCode.StalePlan, "The source DAT changed since the plan was frozen.", lf.Plan.LeafId);

                // Source SHA-256 (written later to dat_lines.source_dat_sha256; not compared — the plan has no hash).
                lf.Sha256        = ComputeSha256(srcAbs);
                // Build strictly from the FROZEN snapshot games (verified equal to the current parse above).
                lf.Games         = ToParsedGames(lf.Snapshot.Games);
                lf.ReleaseCount  = lf.Snapshot.Games.Length;
                lf.FileCount     = lf.Snapshot.Games.Sum(g => g.Roms.Length);
                lf.WorkingStates = lf.Snapshot.Games
                    .Where(g => g.WorkingState.Length > 0)
                    .Select(g => new GroupDatInitialWorkingState(g.Name, g.WorkingState))
                    .ToList();
            }

            // ---- Part 7: PREPARE all leaf databases into temp files (no catalog rows yet) ----
            ct.ThrowIfCancellationRequested();
            idx = 0;
            foreach (var lf in leaves)
            {
                idx++;
                currentLeaf = lf.Plan.LeafId;
                ct.ThrowIfCancellationRequested();
                progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.Preparing, idx, total, lf.Plan.LeafId, $"Preparing leaf {idx} / {total}"));

                var prepared    = LeafDatDatabaseBuilder.Prepare(lf.Plan.LeafId, lf.Games, ct);
                var buildResult = LeafDatDatabaseBuilder.Build(lf.TempAbsPath, prepared, null, ct);
                manifest.Temps.Add(lf.TempAbsPath);   // temp now exists on disk — track for cleanup

                if (buildResult.VerifiedReleaseCount != lf.ReleaseCount ||
                    buildResult.VerifiedReleaseFileCount != lf.FileCount)
                    throw new InvalidOperationException($"Leaf '{lf.Plan.LeafId}' failed post-build verification.");

                OnLeafPreparedForTests?.Invoke(idx);   // test seam
            }

            // ---- Part 9: verify-all barrier — every leaf is now prepared AND verified. Only now publish. ----

            // ---- Part 10: PUBLISH temp → final (rename, no overwrite) ----
            ct.ThrowIfCancellationRequested();
            idx = 0;
            foreach (var lf in leaves)
            {
                idx++;
                currentLeaf = lf.Plan.LeafId;
                ct.ThrowIfCancellationRequested();   // cancelled here ⇒ outer catch cleans temps + finals
                progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.Publishing, idx, total, lf.Plan.LeafId, $"Publishing leaf {idx} / {total}"));

                File.Move(lf.TempAbsPath, lf.FinalAbsPath);   // default overload never overwrites
                manifest.Temps.Remove(lf.TempAbsPath);
                manifest.Finals.Add((lf.FinalAbsPath, lf.FinalRelPath));

                OnLeafPublishedForTests?.Invoke(idx);   // test seam
            }

            // ---- Part 11: build the atomic catalog request (all final DBs now exist) ----
            ct.ThrowIfCancellationRequested();
            currentLeaf = null;
            var request = BuildCatalogRequest(plan, leaves);

            // ---- Part 12: single atomic catalog commit ----
            progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.CommittingCatalog, total, total, null, "Committing catalog"));
            try
            {
                _catalog.CreateDatGroupWithLeaves(request, ct);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before/within the catalog commit ⇒ M2 rolled back ⇒ remove this execution's final DBs.
                return CleanupAndBuildResult(groupId, total, manifest, GroupDatExecutionStatus.Cancelled,
                    GroupDatExecutionErrorCode.Cancelled, "Cancelled before the catalog commit completed.", null, progress);
            }
            catch (GroupDatCatalogValidationException vex)
            {
                return CleanupAndBuildResult(groupId, total, manifest, GroupDatExecutionStatus.AbortedNoWrites,
                    GroupDatExecutionErrorCode.CatalogFailed, $"Catalog validation failed: {vex.Error}.", vex.LeafId, progress);
            }
            catch (Exception)
            {
                return CleanupAndBuildResult(groupId, total, manifest, GroupDatExecutionStatus.AbortedNoWrites,
                    GroupDatExecutionErrorCode.CatalogFailed, "The catalog commit failed.", null, progress);
            }

            // ---- Success: the Group is now visible. Do NOT clean up the final DBs. ----
            progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.Completed, total, total, null, "Completed"));
            return new GroupDatExecutionResult
            {
                GroupId        = groupId,
                OverallStatus  = GroupDatExecutionStatus.Committed,
                LeafTotal      = total,
                PreparedCount  = total,
                PublishedCount = total,
                Revision       = 0,
                ErrorCode      = GroupDatExecutionErrorCode.None,
            };
        }
        catch (OperationCanceledException)
        {
            // Cancelled during revalidation / prepare / publish (before the commit) — clean this execution's files.
            return CleanupAndBuildResult(groupId, total, manifest, GroupDatExecutionStatus.Cancelled,
                GroupDatExecutionErrorCode.Cancelled, "Cancelled.", currentLeaf, progress);
        }
        catch (Exception)
        {
            // Prepare/publish (or verification) failure before the commit — catalog untouched; clean this execution.
            var code = manifest.Finals.Count > 0
                ? GroupDatExecutionErrorCode.PublishFailed
                : GroupDatExecutionErrorCode.PrepareFailed;
            var msg = manifest.Finals.Count > 0
                ? "Failed to publish a leaf database."
                : "Failed to prepare a leaf database.";
            return CleanupAndBuildResult(groupId, total, manifest, GroupDatExecutionStatus.AbortedNoWrites, code, msg, currentLeaf, progress);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Structural plan validation (Part 2)

    private static GroupDatExecutionResult? ValidatePlanStructure(GroupDatReconciliationPlan plan)
    {
        if (plan is null)
            return new GroupDatExecutionResult { GroupId = "", OverallStatus = GroupDatExecutionStatus.AbortedNoWrites, LeafTotal = 0, ErrorCode = GroupDatExecutionErrorCode.InvalidPlan, ErrorMessage = "The plan is null." };

        string gid = plan.GroupId ?? "";
        int total  = plan.NewLeaves.IsDefault ? 0 : plan.NewLeaves.Length;

        GroupDatExecutionResult Bad(string m) => Abort(gid, total, GroupDatExecutionErrorCode.InvalidPlan, m);

        if (plan.Mode != GroupDatReconciliationMode.NewGroup)         return Bad("The plan is not a Create (NewGroup) plan.");
        if (!DatTechnicalIdPolicy.IsValidNew(gid))                    return Bad("The group id is missing or invalid.");
        if (string.IsNullOrWhiteSpace(plan.GroupName))               return Bad("The group name is missing.");
        if (string.IsNullOrWhiteSpace(plan.SystemId))               return Bad("The System id is missing.");
        if (string.IsNullOrWhiteSpace(plan.HardwareFamilyId) ||
            !string.Equals(plan.HardwareFamilyId, plan.SystemId, StringComparison.OrdinalIgnoreCase))
                                                                      return Bad("The hardware family id is inconsistent with the System id.");
        if (string.IsNullOrWhiteSpace(plan.Authority))              return Bad("The authority is missing.");
        if (plan.NewLeaves.IsDefaultOrEmpty)                          return Bad("The plan has no new leaves.");
        if (!plan.Updates.IsDefaultOrEmpty)                          return Bad("A Create plan must have no updates.");
        if (!plan.AbsentLeaves.IsDefaultOrEmpty)                     return Bad("A Create plan must have no absent leaves.");
        if (plan.DiscoverySnapshot.IsDefaultOrEmpty)                  return Bad("The discovery snapshot is unavailable.");

        return null;
    }

    // ---------------------------------------------------------------------------------------------------
    // Source path validation + reparse-point policy (Parts 3) — mirrors discovery's semantics.

    private static string? ValidateSourcePath(string root, string relativePath, out string message, out GroupDatExecutionErrorCode code)
    {
        message = "";
        code    = GroupDatExecutionErrorCode.None;

        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith("..", StringComparison.Ordinal) ||
            relativePath.Split('/', '\\').Any(seg => seg is ".." or "."))
        {
            message = "The source relative path is invalid.";
            code    = GroupDatExecutionErrorCode.SourcePathInvalid;
            return null;
        }

        var abs        = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!abs.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            message = "The source path escapes the source root.";
            code    = GroupDatExecutionErrorCode.SourcePathInvalid;
            return null;
        }

        if (PathTraversesReparsePoint(root, abs))
        {
            message = "The source path traverses a reparse point.";
            code    = GroupDatExecutionErrorCode.ReparsePoint;
            return null;
        }

        if (!File.Exists(abs))
        {
            message = "The source DAT file no longer exists.";
            code    = GroupDatExecutionErrorCode.SourceMissing;
            return null;
        }

        return abs;
    }

    /// <summary>True if the file itself, or any directory between the source root (exclusive) and the file,
    /// is a reparse point (symlink/junction). Conservative on error, matching discovery's policy.</summary>
    private static bool PathTraversesReparsePoint(string root, string absFile)
    {
        try
        {
            var fi = new FileInfo(absFile);
            if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0) return true;

            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var dir      = new DirectoryInfo(Path.GetDirectoryName(absFile)!);
            while (dir is not null &&
                   !string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase))
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                dir = dir.Parent;
            }
            return false;
        }
        catch
        {
            return true;   // if attributes cannot be read, treat as a link (block)
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Semantic snapshot comparison (Part 3) — case/ordering-sensitive Ordinal, exactly as the parser/model
    // produce (no new normalization).

    private static bool SnapshotMatches(DiscoveredDatLeaf snap, DatParser.Result parsed)
    {
        if (!string.Equals(snap.DatName,    parsed.Name,    StringComparison.Ordinal)) return false;
        if (!string.Equals(snap.DatVersion, parsed.Version, StringComparison.Ordinal)) return false;
        if (snap.Games.Length != parsed.Games.Count) return false;

        for (int i = 0; i < snap.Games.Length; i++)
        {
            var s = snap.Games[i];
            var p = parsed.Games[i];
            if (!string.Equals(s.Name,         p.Name,         StringComparison.Ordinal)) return false;
            if (!string.Equals(s.Region,       p.Region,       StringComparison.Ordinal)) return false;
            if (!string.Equals(s.Languages,    p.Languages,    StringComparison.Ordinal)) return false;
            if (!string.Equals(s.ContentKey,   p.ContentKey,   StringComparison.Ordinal)) return false;
            if (!string.Equals(s.WorkingState, p.WorkingState, StringComparison.Ordinal)) return false;
            if (s.Roms.Length != p.Roms.Count) return false;

            for (int j = 0; j < s.Roms.Length; j++)
            {
                var sr = s.Roms[j];
                var pr = p.Roms[j];
                if (!string.Equals(sr.Name, pr.Name, StringComparison.Ordinal)) return false;
                if (!string.Equals(sr.Size, pr.Size, StringComparison.Ordinal)) return false;
                if (!string.Equals(sr.Crc,  pr.Crc,  StringComparison.Ordinal)) return false;
                if (!string.Equals(sr.Md5,  pr.Md5,  StringComparison.Ordinal)) return false;
                if (!string.Equals(sr.Sha1, pr.Sha1, StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }

    private static List<DatParser.ParsedGame> ToParsedGames(ImmutableArray<DiscoveredDatGame> games)
        => games.Select(g => new DatParser.ParsedGame
        {
            Name         = g.Name,
            Region       = g.Region,
            Languages    = g.Languages,
            ContentKey   = g.ContentKey,
            WorkingState = g.WorkingState,
            Roms         = g.Roms.Select(r => new DatParser.ParsedRom
            {
                Name = r.Name, Size = r.Size, Crc = r.Crc, Md5 = r.Md5, Sha1 = r.Sha1,
            }).ToList(),
        }).ToList();

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------------------------------------
    // Catalog request (Part 11) — reproduces Single-DAT Import semantics for the dat_line row.

    private static GroupDatCatalogCreateRequest BuildCatalogRequest(GroupDatReconciliationPlan plan, List<LeafExec> leaves)
    {
        var leafReqs = new List<GroupDatCatalogLeafCreate>(leaves.Count);
        foreach (var lf in leaves)
        {
            var datLine = new DatLineRecord
            {
                Id                = lf.Plan.LeafId,
                HardwareFamilyId  = plan.HardwareFamilyId,
                Name              = lf.Plan.MediaTypeId,   // Single-DAT Import sets Name = mediaTypeId
                Authority         = plan.Authority,
                MediaTypeId       = lf.Plan.MediaTypeId,
                Version           = lf.Plan.Version,
                StorageStrategyId = "",
                DataStorePath     = lf.FinalRelPath,
                ReleaseCount      = lf.ReleaseCount,
                ImportedAtUtc     = DateTime.UtcNow,
                // TransformStrategyType / FolderTransformId / FileHandling / CatalogEnabled / LibraryTitleMode:
                // left at DatLineRecord defaults, exactly as Single-DAT Import does.
            };

            leafReqs.Add(new GroupDatCatalogLeafCreate
            {
                DatLine                    = datLine,
                RelativeDatPath            = lf.Plan.SourceRelativePath,   // already normalized to '/'
                SourceDatName              = lf.Snapshot.FileName,
                SourceDatSha256            = lf.Sha256,
                SemanticFingerprint        = null,   // v1
                SemanticFingerprintVersion = null,   // v1
                LastSeenGroupRevision      = 0,      // v1
                InitialWorkingStates       = lf.WorkingStates,
            });
        }

        return new GroupDatCatalogCreateRequest
        {
            GroupId          = plan.GroupId,
            DisplayName      = plan.GroupName,
            HardwareFamilyId = plan.HardwareFamilyId,
            Authority        = plan.Authority,
            Leaves           = leafReqs,
        };
    }

    // ---------------------------------------------------------------------------------------------------
    // Cleanup (Parts 8/10/13) — best-effort removal of ONLY this execution's files, from the in-memory
    // manifest, never from directory scans; final DBs referenced by a catalog dat_line are never deleted.

    private GroupDatExecutionResult CleanupAndBuildResult(
        string groupId, int total, ExecManifest m,
        GroupDatExecutionStatus cleanStatus, GroupDatExecutionErrorCode code, string? message, string? leafId,
        IProgress<GroupDatExecutionProgress>? progress)
    {
        int prepared  = m.Temps.Count + m.Finals.Count;
        int published = m.Finals.Count;

        progress?.Report(new GroupDatExecutionProgress(GroupDatExecutionPhase.CleaningUp, 0, total, leafId, "Cleaning up"));
        var remaining = CleanupThisExecution(m);

        var status = remaining.Count == 0 ? cleanStatus : GroupDatExecutionStatus.CleanupRequired;
        return new GroupDatExecutionResult
        {
            GroupId        = groupId,
            OverallStatus  = status,
            LeafTotal      = total,
            PreparedCount  = prepared,
            PublishedCount = published,
            Revision       = null,
            ErrorCode      = code,
            ErrorMessage   = message,
            LeafId         = leafId,
            CleanupPaths   = remaining,
        };
    }

    private List<string> CleanupThisExecution(ExecManifest m)
    {
        var remaining = new List<string>();

        foreach (var temp in m.Temps.ToList())
        {
            if (TryDelete(temp)) m.Temps.Remove(temp);
            else                 remaining.Add(temp);
        }

        // A final DB is only removed if NO catalog dat_line references its DataStorePath (defence in depth:
        // on any pre-commit failure the catalog is unchanged, so our finals are never registered).
        var registered = new HashSet<string>(_catalog.LoadDatLines().Select(d => d.DataStorePath), StringComparer.OrdinalIgnoreCase);
        foreach (var fin in m.Finals.ToList())
        {
            if (registered.Contains(fin.RelPath)) { remaining.Add(fin.AbsPath); continue; }   // catalogued ⇒ never delete
            if (TryDelete(fin.AbsPath)) m.Finals.Remove(fin);
            else                        remaining.Add(fin.AbsPath);
        }

        return remaining;
    }

    private bool TryDelete(string absPath)
    {
        if (TryDeleteOverrideForTests is not null) return TryDeleteOverrideForTests(absPath);
        try
        {
            if (File.Exists(absPath)) File.Delete(absPath);
            foreach (var side in new[] { absPath + "-wal", absPath + "-shm" })
                if (File.Exists(side)) File.Delete(side);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static GroupDatExecutionResult Abort(string groupId, int total, GroupDatExecutionErrorCode code, string message, string? leafId = null)
        => new()
        {
            GroupId       = groupId,
            OverallStatus = GroupDatExecutionStatus.AbortedNoWrites,
            LeafTotal     = total,
            Revision      = null,
            ErrorCode     = code,
            ErrorMessage  = message,
            LeafId        = leafId,
        };

    // ---------------------------------------------------------------------------------------------------

    private sealed class LeafExec
    {
        public required GroupDatNewLeafPlan Plan         { get; init; }
        public required DiscoveredDatLeaf   Snapshot     { get; init; }
        public required string              FinalRelPath { get; init; }   // systems/<system>/<leaf>.db
        public required string              FinalAbsPath { get; init; }
        public required string              TempAbsPath  { get; init; }   // <final>.tmp-<exec-id>

        public string                            Sha256        { get; set; } = "";
        public List<DatParser.ParsedGame>        Games         { get; set; } = new();
        public int                               ReleaseCount  { get; set; }
        public int                               FileCount     { get; set; }
        public List<GroupDatInitialWorkingState> WorkingStates { get; set; } = new();
    }

    private sealed class ExecManifest
    {
        public readonly List<string>                        Temps  = new();   // temp DBs created, not yet published
        public readonly List<(string AbsPath, string RelPath)> Finals = new(); // temp→final renames performed
    }
}
