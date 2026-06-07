using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Full-scan Verify Volume service.
///
/// Recursively scans the entire volume root, classifies every physical file
/// by hash, executes recovery moves, and updates the DB.
///
/// Volume layout invariant: active artifacts must be flat in the volume root.
///   CORRECT:   &lt;volume root&gt;\artifact.chd
///   WRONG:     &lt;volume root&gt;\Release Name\artifact.chd
/// </summary>
public sealed class VolumeVerifyService
{
    // ── Managed folder names ──────────────────────────────────────────────────

    /// <summary>
    /// First-level subdirectory names that hold managed (non-active) files.
    /// Files under these folders are not classified as active volume content.
    /// </summary>
    public static readonly IReadOnlySet<string> ManagedFolderNames =
        new HashSet<string>(["unwanted", "known", "unknown"], StringComparer.OrdinalIgnoreCase);

    // ── Arkadia system files allowlist ────────────────────────────────────────

    /// <summary>
    /// File names that are Arkadia-managed and must never be moved to unknown\.
    /// </summary>
    public static readonly IReadOnlySet<string> SystemFileNames =
        new HashSet<string>(["ARKADIA.DISK.json"], StringComparer.OrdinalIgnoreCase);

    private readonly CatalogService _catalog;

    public VolumeVerifyService(CatalogService catalog) => _catalog = catalog;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a full recursive scan of <paramref name="volumeRoot"/>,
    /// classifies every file, executes recovery moves, and updates the DB.
    /// </summary>
    /// <param name="volumeId">Catalog volume ID being verified.</param>
    /// <param name="volumeRoot">Absolute path to the mounted volume root.</param>
    /// <param name="store">Open DatLineStore for the volume's DAT line.</param>
    /// <param name="allDatLineDbPaths">
    ///     Paths to ALL known DAT-line SQLite files, used for cross-volume SHA1 lookup.
    /// </param>
    public VolumeVerifyResult Verify(
        string                          volumeId,
        string                          volumeRoot,
        DatLineStore                    store,
        IReadOnlyList<string>           allDatLineDbPaths,
        IProgress<FoundFileProgress>?   foundFileProgress = null)
    {
        var log = new List<string>();

        // ── Build owned-artifact maps ─────────────────────────────────────────
        var vaRecords = _catalog.GetVolumeArtifacts(volumeId);
        var vaByDaId  = vaRecords.ToDictionary(va => va.DerivedArtifactId, StringComparer.Ordinal);

        var verifyInfos = store.GetArtifactVerifyInfos(vaByDaId.Keys);
        var ownedBySha1 = new Dictionary<string, ArtifactVerifyInfo>(StringComparer.OrdinalIgnoreCase);
        var ownedByDaId = new Dictionary<string, ArtifactVerifyInfo>(StringComparer.Ordinal);
        foreach (var vi in verifyInfos)
        {
            if (vi.Sha1.Length > 0) ownedBySha1.TryAdd(vi.Sha1, vi);
            ownedByDaId[vi.DerivedArtifactId] = vi;
        }

        // ── Recursive scan ────────────────────────────────────────────────────
        log.Add("recursive-scan-start");
        var physFiles = ScanFiles(volumeRoot, log, foundFileProgress);
        log.Add($"recursive-scan-complete  total={physFiles.Count}");

        // ── Classify each active-area file ────────────────────────────────────
        foreach (var pf in physFiles)
        {
            if (pf.IsInManagedFolder) continue;

            if (SystemFileNames.Contains(pf.FileName))
            {
                pf.Classification = VolumeFileClass.SystemFile;
                log.Add($"system-file-ok  {pf.RelativePath}");
                continue;
            }

            pf.Sha1 = ComputeSha1(pf.FullPath);
            ClassifyFile(pf, ownedBySha1, vaByDaId, store, allDatLineDbPaths, volumeId);
        }

        // ── Identify missing expected artifacts ───────────────────────────────
        var foundDaIds = physFiles
            .Where(f => !f.IsInManagedFolder && f.DerivedArtifactId is not null)
            .Select(f => f.DerivedArtifactId!)
            .ToHashSet(StringComparer.Ordinal);
        var missingVis = verifyInfos
            .Where(vi => !foundDaIds.Contains(vi.DerivedArtifactId))
            .ToList();
        foreach (var vi in missingVis)
            log.Add($"missing  {vi.FileName}");

        // ── Execute recovery moves ────────────────────────────────────────────
        int misplacedFound = 0, misplacedRestored = 0, misplacedCollisions = 0;
        int unwantedFound  = 0, unwantedMoved = 0;
        int knownFound     = 0, knownMoved    = 0;
        int unknownFound   = 0, unknownMoved  = 0;
        int sysCount       = 0, errors        = 0;

        var presentDaIds = new List<string>();
        var badDaIds     = missingVis.Select(vi => vi.DerivedArtifactId).ToList();

        foreach (var pf in physFiles.Where(f => !f.IsInManagedFolder))
        {
            switch (pf.Classification)
            {
                case VolumeFileClass.SystemFile:
                    sysCount++;
                    break;

                case VolumeFileClass.OkWanted:
                    log.Add($"verify-ok  {pf.RelativePath}  sha1={pf.Sha1}");
                    presentDaIds.Add(pf.DerivedArtifactId!);
                    break;

                case VolumeFileClass.MisplacedWanted:
                    misplacedFound++;
                    log.Add($"misplaced-found  {pf.RelativePath}  canonical={pf.CanonicalFileName}");
                    if (TryRestoreMisplaced(pf, volumeRoot, log))
                    {
                        misplacedRestored++;
                        presentDaIds.Add(pf.DerivedArtifactId!);
                        TryRemoveEmptyDir(Path.GetDirectoryName(pf.FullPath)!, volumeRoot);
                    }
                    else
                    {
                        misplacedCollisions++;
                        errors++;
                        badDaIds.Add(pf.DerivedArtifactId!);
                    }
                    break;

                case VolumeFileClass.UnwantedFound:
                    unwantedFound++;
                    log.Add($"unwanted-found  {pf.RelativePath}");
                    if (TryMoveToManaged(pf.FullPath, Path.Combine(volumeRoot, "unwanted"), pf.FileName, log, "unwanted-moved"))
                    {
                        unwantedMoved++;
                        // Remove VA row and decrement usage if it was counted as active
                        if (pf.DerivedArtifactId is not null
                            && vaByDaId.TryGetValue(pf.DerivedArtifactId, out var va)
                            && ownedByDaId.TryGetValue(pf.DerivedArtifactId, out var vi))
                        {
                            _catalog.DeleteVolumeArtifactRow(va.Id, volumeId, vi.SizeBytes);
                            log.Add($"usage-refreshed  volume={volumeId}  removed-bytes={vi.SizeBytes}");
                        }
                    }
                    TryRemoveEmptyDir(Path.GetDirectoryName(pf.FullPath)!, volumeRoot);
                    break;

                case VolumeFileClass.KnownUnexpected:
                    knownFound++;
                    var volLabel = pf.ExpectedVolumeLabel ?? "unknown-volume";
                    var knownDir = Path.Combine(volumeRoot, "known", MakeSafeSegment(volLabel));
                    log.Add($"known-unexpected-found  {pf.RelativePath}  expected-volume={volLabel}");
                    if (TryMoveToManaged(pf.FullPath, knownDir, pf.FileName, log, "known-unexpected-moved"))
                        knownMoved++;
                    TryRemoveEmptyDir(Path.GetDirectoryName(pf.FullPath)!, volumeRoot);
                    break;

                case VolumeFileClass.UnknownFile:
                    unknownFound++;
                    log.Add($"unknown-found  {pf.RelativePath}");
                    if (TryMoveToManaged(pf.FullPath, Path.Combine(volumeRoot, "unknown"), pf.FileName, log, "unknown-moved"))
                        unknownMoved++;
                    TryRemoveEmptyDir(Path.GetDirectoryName(pf.FullPath)!, volumeRoot);
                    break;
            }
        }

        // ── Apply DB state updates ────────────────────────────────────────────
        if (presentDaIds.Count > 0)
            store.BatchUpdateDerivedArtifactStatus(presentDaIds, "present");
        if (badDaIds.Count > 0)
            store.BatchUpdateDerivedArtifactStatus(badDaIds, "missing");

        var allChanged = new List<string>(presentDaIds.Count + badDaIds.Count);
        allChanged.AddRange(presentDaIds);
        allChanged.AddRange(badDaIds);
        if (allChanged.Count > 0)
            store.RecalculateReleaseStatusForArtifacts(allChanged);

        // ── Compute final health ──────────────────────────────────────────────
        bool isHealthy =
            missingVis.Count    == 0 &&
            misplacedCollisions == 0 &&
            (unknownFound - unknownMoved) == 0 &&
            (knownFound   - knownMoved)   == 0 &&
            (unwantedFound - unwantedMoved) == 0 &&
            errors == 0;

        bool hadRecovery = (misplacedRestored + unwantedMoved + knownMoved + unknownMoved) > 0;

        log.Add($"summary  scanned={physFiles.Count}  sys={sysCount}  verified={presentDaIds.Count}  " +
                $"misplaced-found={misplacedFound}  restored={misplacedRestored}  " +
                $"unwanted={unwantedFound}  unwanted-moved={unwantedMoved}  " +
                $"known={knownFound}  known-moved={knownMoved}  " +
                $"unknown={unknownFound}  unknown-moved={unknownMoved}  " +
                $"missing={missingVis.Count}  errors={errors}  healthy={isHealthy}");

        return new VolumeVerifyResult
        {
            TotalScanned         = physFiles.Count,
            SystemFiles          = sysCount,
            Verified             = presentDaIds.Count,
            MisplacedFound       = misplacedFound,
            MisplacedRestored    = misplacedRestored,
            MisplacedCollisions  = misplacedCollisions,
            UnwantedFound        = unwantedFound,
            UnwantedMoved        = unwantedMoved,
            KnownUnexpectedFound = knownFound,
            KnownUnexpectedMoved = knownMoved,
            UnknownFound         = unknownFound,
            UnknownMoved         = unknownMoved,
            Missing              = missingVis.Count,
            Errors               = errors,
            IsHealthy            = isHealthy,
            HadRecoveryActions   = hadRecovery,
            LogLines             = log,
        };
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private static List<PhysicalVolumeFile> ScanFiles(
        string volumeRoot, List<string> log,
        IProgress<FoundFileProgress>? foundFileProgress)
    {
        var result = new List<PhysicalVolumeFile>();
        if (!Directory.Exists(volumeRoot)) return result;

        foreach (var fullPath in Directory.EnumerateFiles(volumeRoot, "*", SearchOption.AllDirectories))
        {
            var rel      = Path.GetRelativePath(volumeRoot, fullPath);
            var fileName = Path.GetFileName(fullPath);
            var topDir   = rel.Contains(Path.DirectorySeparatorChar)
                           ? rel[..rel.IndexOf(Path.DirectorySeparatorChar)]
                           : "";

            bool inManaged = topDir.Length > 0 && ManagedFolderNames.Contains(topDir);
            bool inRoot    = topDir.Length == 0;
            long size      = 0;
            try { size = new FileInfo(fullPath).Length; } catch { /* skip unreadable */ }

            result.Add(new PhysicalVolumeFile
            {
                FullPath          = fullPath,
                RelativePath      = rel,
                FileName          = fileName,
                SizeBytes         = size,
                IsInRoot          = inRoot,
                IsInManagedFolder = inManaged,
            });

            // Emit found-file progress for active-area files only (not managed-folder files).
            // This is neutral discovery — does not affect any verify counter.
            if (!inManaged)
                foundFileProgress?.Report(new FoundFileProgress(rel, fullPath, size));
        }

        return result;
    }

    // ── Classification ────────────────────────────────────────────────────────

    private void ClassifyFile(
        PhysicalVolumeFile              pf,
        Dictionary<string, ArtifactVerifyInfo> ownedBySha1,
        Dictionary<string, VolumeArtifactRecord> vaByDaId,
        DatLineStore                    store,
        IReadOnlyList<string>           allDatLineDbPaths,
        string                          volumeId)
    {
        // FindArtifactBySha1 is the authoritative source — it returns isUnwanted.
        // We check the current DAT-line first (fastest, covers 99% of cases).
        var found = store.FindArtifactBySha1(pf.Sha1);

        if (found is null)
        {
            // Try other DAT-line DBs
            foreach (var dbPath in allDatLineDbPaths)
            {
                if (!File.Exists(dbPath)) continue;
                try
                {
                    found = new DatLineStore(dbPath).FindArtifactBySha1(pf.Sha1);
                    if (found is not null) break;
                }
                catch { /* unreadable DB — skip */ }
            }
        }

        if (found is null)
        {
            pf.Classification = VolumeFileClass.UnknownFile;
            return;
        }

        var (daId, fileName, isUnwanted) = found.Value;
        pf.DerivedArtifactId = daId;
        pf.CanonicalFileName = fileName;

        // Unwanted releases: always move out of active area regardless of ownership
        if (isUnwanted)
        {
            pf.Classification = VolumeFileClass.UnwantedFound;
            // Also populate VA info so we can remove the row + decrement usage
            if (ownedBySha1.TryGetValue(pf.Sha1, out var uwVi))
            {
                pf.ArtifactSizeBytes = uwVi.SizeBytes;
                vaByDaId.TryGetValue(daId, out var uwVa);
                pf.VolumeArtifactId = uwVa?.Id;
            }
            return;
        }

        // Is this artifact owned (assigned) by the current volume?
        if (ownedBySha1.TryGetValue(pf.Sha1, out var vi))
        {
            vaByDaId.TryGetValue(vi.DerivedArtifactId, out var va);
            pf.DerivedArtifactId = vi.DerivedArtifactId;
            pf.VolumeArtifactId  = va?.Id;
            pf.ArtifactSizeBytes = vi.SizeBytes;
            pf.CanonicalFileName = vi.FileName;

            bool atFlatRoot = pf.IsInRoot &&
                string.Equals(pf.FileName, vi.FileName, StringComparison.OrdinalIgnoreCase);
            pf.Classification = atFlatRoot ? VolumeFileClass.OkWanted : VolumeFileClass.MisplacedWanted;
            return;
        }

        // Known artifact from another volume
        var owningVols = _catalog.GetOwningVolumesForArtifact(daId, volumeId);
        pf.Classification      = VolumeFileClass.KnownUnexpected;
        pf.ExpectedVolumeId    = owningVols.Count > 0 ? owningVols[0].VolumeId    : null;
        pf.ExpectedVolumeLabel = owningVols.Count > 0 ? owningVols[0].VolumeLabel : null;
    }

    // ── Recovery moves ────────────────────────────────────────────────────────

    private static bool TryRestoreMisplaced(PhysicalVolumeFile pf, string volumeRoot, List<string> log)
    {
        var target = VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, pf.CanonicalFileName!);
        if (File.Exists(target))
        {
            log.Add($"collision  misplaced-restore  {pf.RelativePath}  target={target}");
            return false;
        }

        try
        {
            File.Move(pf.FullPath, target);
            log.Add($"misplaced-restored  {pf.RelativePath}  →  {Path.GetFileName(target)}");
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"error  misplaced-restore  {pf.RelativePath}  {ex.Message}");
            return false;
        }
    }

    private static bool TryMoveToManaged(
        string fullPath, string targetDir, string fileName,
        List<string> log, string successTag)
    {
        Directory.CreateDirectory(targetDir);
        var target = GetCollisionSafePath(targetDir, fileName);

        if (File.Exists(target))
        {
            log.Add($"collision  {successTag}  could not find safe name in {targetDir}");
            return false;
        }

        try
        {
            File.Move(fullPath, target);
            log.Add($"{successTag}  {Path.GetFileName(fullPath)}  →  {target}");
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"error  {successTag}  {ex.Message}");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void TryRemoveEmptyDir(string dir, string volumeRoot)
    {
        // Never remove the volume root itself
        if (string.Equals(dir, volumeRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(dir)) return;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* non-fatal */ }
    }

    private static string GetCollisionSafePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (int i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, fileName); // will fail at File.Move — caller catches
    }

    private static string MakeSafeSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    internal static string ComputeSha1(string filePath)
    {
        using var fs   = File.OpenRead(filePath);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
    }
}
