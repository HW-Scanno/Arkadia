using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Dry-run planner for the Append Volume operation.
///
/// Finds unassigned archive artifacts for the same DAT line as the selected volume,
/// verifies physical archive files exist, and applies capacity constraints.
/// </summary>
public sealed class AppendVolumePlanner
{
    private readonly CatalogService _catalog;

    public AppendVolumePlanner(CatalogService catalog) => _catalog = catalog;

    // ── Skip-reason codes ─────────────────────────────────────────────────────

    public static class SkipReason
    {
        public const string AlreadyAssigned                 = "AlreadyAssigned";
        public const string ArchiveMissing                  = "ArchiveMissing";
        public const string TargetPathExists                = "TargetPathExists";
        public const string TooLargeForRemainingTargetSpace = "TooLargeForRemainingTargetSpace";
        public const string InvalidHash                     = "InvalidHash";
        public const string ReleaseUnwanted                 = "ReleaseUnwanted";
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an append plan for <paramref name="volume"/>.
    /// <paramref name="archiveRoot"/> is the application base directory from which
    /// <c>derived_artifacts.relative_path</c> values are resolved.
    /// </summary>
    public AppendVolumePlan Plan(
        VolumeRecord volume,
        string       volumeRootPath,
        string       archiveRoot,
        DatLineStore store)
    {
        // ── Hard precondition ─────────────────────────────────────────────
        if (!Directory.Exists(volumeRootPath))
            return Empty(volume, volumeRootPath);

        // ── DB candidates (all non-unwanted artifacts in this DAT line) ───
        var candidates       = store.GetAllWantedArtifactInfos();
        int releaseUnwanted  = store.GetUnwantedArtifactCount();
        var assignedDaIds    = _catalog.GetAssignedDerivedIdsByDatLine(volume.DatLineId);

        long targetFree = volume.PlannedSizeBytes - volume.ActualSizeBytes;
        long allocated  = 0;

        var entries          = new List<AppendEntry>();
        var skipReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (releaseUnwanted > 0)
            skipReasonCounts[SkipReason.ReleaseUnwanted] = releaseUnwanted;

        int alreadyAssigned = 0, archiveMissing = 0, targetCollision = 0,
            tooLarge = 0, invalidHash = 0;

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.ExpectedSha1))
            {
                entries.Add(MakeSkip(c, "", "",
                    $"{SkipReason.InvalidHash}: SHA1 is empty"));
                Increment(skipReasonCounts, SkipReason.InvalidHash);
                invalidHash++;
                continue;
            }

            if (assignedDaIds.Contains(c.DerivedArtifactId))
            {
                entries.Add(MakeSkip(c, "", "", SkipReason.AlreadyAssigned));
                Increment(skipReasonCounts, SkipReason.AlreadyAssigned);
                alreadyAssigned++;
                continue;
            }

            var archivePath = Path.Combine(archiveRoot,
                c.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetPath  = VolumeArtifactPathBuilder.GetFlatFullPath(volumeRootPath, c.FileName);

            if (!File.Exists(archivePath))
            {
                entries.Add(MakeSkip(c, archivePath, targetPath, SkipReason.ArchiveMissing));
                Increment(skipReasonCounts, SkipReason.ArchiveMissing);
                archiveMissing++;
                continue;
            }

            if (File.Exists(targetPath))
            {
                var reason = $"{SkipReason.TargetPathExists}: {c.FileName} already exists at target";
                entries.Add(MakeSkip(c, archivePath, targetPath, reason));
                Increment(skipReasonCounts, SkipReason.TargetPathExists);
                targetCollision++;
                continue;
            }

            if (c.SizeBytes > targetFree - allocated)
            {
                var reason = $"{SkipReason.TooLargeForRemainingTargetSpace}: needs {FormatBytes(c.SizeBytes)}, remaining {FormatBytes(targetFree - allocated)}";
                entries.Add(MakeSkip(c, archivePath, targetPath, reason));
                Increment(skipReasonCounts, SkipReason.TooLargeForRemainingTargetSpace);
                tooLarge++;
                continue;
            }

            entries.Add(new AppendEntry
            {
                DerivedArtifactId  = c.DerivedArtifactId,
                ContentIdentityKey = c.ContentIdentityKey,
                ReleaseName        = c.ReleaseName,
                FileName           = c.FileName,
                SizeBytes          = c.SizeBytes,
                ExpectedSha1       = c.ExpectedSha1,
                ArchivePath        = archivePath,
                TargetPath         = targetPath,
                Action             = AppendEntryAction.Copy,
                Reason             = "",
            });
            allocated += c.SizeBytes;
        }

        int plannedCount = entries.Count(e => e.Action == AppendEntryAction.Copy);
        int skippedCount = entries.Count(e => e.Action == AppendEntryAction.Skip);

        return new AppendVolumePlan
        {
            VolumeId              = volume.Id,
            VolumeLabel           = volume.Label,
            DatLineId             = volume.DatLineId,
            VolumeRootPath        = volumeRootPath,
            TargetCapacityBytes   = volume.PlannedSizeBytes,
            TargetUsedBytes       = volume.ActualSizeBytes,
            TargetFreeBytes       = targetFree,
            PlannedCount          = plannedCount,
            PlannedBytes          = allocated,
            SkippedCount          = skippedCount,
            RemainingTargetFreeBytes = targetFree - allocated,
            TargetBytesAfter      = volume.ActualSizeBytes + allocated,
            TotalCandidates       = candidates.Count,
            AlreadyAssignedSkipped = alreadyAssigned,
            ArchiveMissingSkipped  = archiveMissing,
            TargetCollisionSkipped = targetCollision,
            TooLargeSkipped         = tooLarge,
            InvalidHashSkipped      = invalidHash,
            ReleaseUnwantedSkipped  = releaseUnwanted,
            Entries               = entries,
            SkipReasonCounts      = skipReasonCounts,
            CanExecute            = plannedCount > 0,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppendVolumePlan Empty(VolumeRecord volume, string volumeRoot) => new()
    {
        VolumeId              = volume.Id,
        VolumeLabel           = volume.Label,
        DatLineId             = volume.DatLineId,
        VolumeRootPath        = volumeRoot,
        TargetCapacityBytes   = volume.PlannedSizeBytes,
        TargetUsedBytes       = volume.ActualSizeBytes,
        TargetFreeBytes       = volume.PlannedSizeBytes - volume.ActualSizeBytes,
        PlannedCount          = 0,
        PlannedBytes          = 0,
        SkippedCount          = 0,
        RemainingTargetFreeBytes = volume.PlannedSizeBytes - volume.ActualSizeBytes,
        TargetBytesAfter      = volume.ActualSizeBytes,
        TotalCandidates       = 0,
        AlreadyAssignedSkipped = 0,
        ArchiveMissingSkipped  = 0,
        TargetCollisionSkipped = 0,
        TooLargeSkipped         = 0,
        InvalidHashSkipped      = 0,
        ReleaseUnwantedSkipped  = 0,
        Entries               = [],
        SkipReasonCounts      = new Dictionary<string, int>(),
        CanExecute            = false,
    };

    private static AppendEntry MakeSkip(AppendCandidateInfo c,
        string archivePath, string targetPath, string reason) => new()
    {
        DerivedArtifactId  = c.DerivedArtifactId,
        ContentIdentityKey = c.ContentIdentityKey,
        ReleaseName        = c.ReleaseName,
        FileName           = c.FileName,
        SizeBytes          = c.SizeBytes,
        ExpectedSha1       = c.ExpectedSha1,
        ArchivePath        = archivePath,
        TargetPath         = targetPath,
        Action             = AppendEntryAction.Skip,
        Reason             = reason,
    };

    private static void Increment(Dictionary<string, int> dict, string key)
        => dict[key] = dict.GetValueOrDefault(key) + 1;

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
