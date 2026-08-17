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
        public const string StaleVolumeAssignment           = "StaleVolumeAssignment";
        public const string ArchiveMissing                  = "ArchiveMissing";
        public const string TargetPathExists                = "TargetPathExists";
        public const string TooLargeForRemainingTargetSpace = "TooLargeForRemainingTargetSpace";
        public const string InvalidHash                     = "InvalidHash";
        public const string InvalidSize                     = "InvalidSize";
        public const string IncomingSkipIgnored             = "IncomingSkipIgnored";
        public const string ReleaseUnwanted                 = "ReleaseUnwanted";
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an append plan for <paramref name="volume"/> (Single owner).
    /// <paramref name="archiveRoot"/> is the application base directory from which
    /// <c>derived_artifacts.relative_path</c> values are resolved. This is the authoritative Single wrapper —
    /// it delegates to <see cref="PlanCore"/> with a zero starting allocation and its behavior is unchanged.
    /// </summary>
    public AppendVolumePlan Plan(
        VolumeRecord volume,
        string       volumeRootPath,
        string       archiveRoot,
        DatLineStore store)
        => PlanCore(
            volume.Id, volume.Label, volume.DatLineId!, volume.PlatformId,
            volume.PlannedSizeBytes, volume.ActualSizeBytes,
            volumeRootPath, archiveRoot, store,
            initialAllocatedBytes: 0, out _);

    /// <summary>
    /// The reusable first-fit planning core over an explicit budget (V-G6 seam). Group Append threads ONE global
    /// remaining-capacity budget across all leaves by passing each leaf's <paramref name="initialAllocatedBytes"/>
    /// = the bytes already committed by earlier leaves, and reading back <paramref name="finalAllocatedBytes"/>.
    /// The Single wrapper calls it with a zero start, so its result is unchanged. The algorithm is identical
    /// first-fit in candidate order — never size-sorted. <paramref name="rootReachableOverride"/> decouples "is
    /// the volume reachable" (Group: the group root exists) from a specific leaf sub-folder's existence; a missing
    /// leaf folder is not a collision and is created at copy time. When null, reachability is the historical
    /// <c>Directory.Exists(volumeRootPath)</c>. <paramref name="datLineId"/> is the OWNER written into every
    /// planned entry — for Group this is the exact leaf id, never the GroupId.
    /// </summary>
    public AppendVolumePlan PlanCore(
        string       volumeId,
        string       volumeLabel,
        string       datLineId,
        string       platformId,
        long         plannedSizeBytes,
        long         actualSizeBytes,
        string       volumeRootPath,
        string       archiveRoot,
        DatLineStore store,
        long         initialAllocatedBytes,
        out long     finalAllocatedBytes,
        bool?        rootReachableOverride = null)
    {
        // ── Hard precondition ─────────────────────────────────────────────
        bool rootReachable = rootReachableOverride ?? Directory.Exists(volumeRootPath);
        if (!rootReachable)
        {
            finalAllocatedBytes = initialAllocatedBytes;
            return Empty(volumeId, volumeLabel, datLineId, platformId,
                         plannedSizeBytes, actualSizeBytes, volumeRootPath, archiveRoot);
        }

        // ── DB candidates (all non-unwanted artifacts in this DAT line) ───
        var candidates       = store.GetAllWantedArtifactInfos();
        int releaseUnwanted  = store.GetUnwantedArtifactCount();
        int totalDa          = candidates.Count + releaseUnwanted;

        // Volume label per assigned DA (value = "(stale assignment)" if volume deleted)
        var assignedWithVolumes = _catalog.GetAssignedDerivedIdsWithVolumesByDatLine(datLineId);

        long targetFree = plannedSizeBytes - actualSizeBytes;
        long allocated  = initialAllocatedBytes;

        // ── Archive physical file count ───────────────────────────────────
        var archiveDir = Path.Combine(archiveRoot, "archive", platformId, datLineId);
        int physicalFileCount = Directory.Exists(archiveDir)
            ? Directory.GetFiles(archiveDir, "*", SearchOption.AllDirectories).Length
            : 0;

        // ── Candidate size range (from all wanted DAs, regardless of skip) ─
        long totalCandidateBytes    = candidates.Sum(c => c.SizeBytes);
        long largestCandidateBytes  = candidates.Count > 0 ? candidates.Max(c => c.SizeBytes) : 0L;
        long smallestCandidateBytes = candidates.Count > 0 ? candidates.Min(c => c.SizeBytes) : 0L;

        // ── Per-entry planning loop ───────────────────────────────────────
        var entries          = new List<AppendEntry>();
        var skipReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (releaseUnwanted > 0)
            skipReasonCounts[SkipReason.ReleaseUnwanted] = releaseUnwanted;

        int alreadyAssigned = 0, archiveMissing = 0, targetCollision = 0,
            tooLarge = 0, invalidHash = 0, incomingSkipIgnored = 0, invalidSize = 0;
        int knownWantedInArchive = 0, unassignedWantedInArchive = 0;

        foreach (var c in candidates)
        {
            // ── Guard: incoming-skip path ─────────────────────────────────
            if (c.RelativePath.StartsWith("incoming-skip/", StringComparison.OrdinalIgnoreCase) ||
                c.RelativePath.StartsWith("incoming-skip\\", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(MakeSkip(c, "", "",
                    $"{SkipReason.IncomingSkipIgnored}: artifact is in quarantine folder",
                    SkipReason.IncomingSkipIgnored));
                Increment(skipReasonCounts, SkipReason.IncomingSkipIgnored);
                incomingSkipIgnored++;
                continue;
            }

            // ── Guard: invalid size ───────────────────────────────────────
            if (c.SizeBytes <= 0)
            {
                entries.Add(MakeSkip(c, "", "",
                    $"{SkipReason.InvalidSize}: artifact has no valid size ({c.SizeBytes} bytes)",
                    SkipReason.InvalidSize));
                Increment(skipReasonCounts, SkipReason.InvalidSize);
                invalidSize++;
                continue;
            }

            // ── Guard: empty SHA1 ─────────────────────────────────────────
            if (string.IsNullOrEmpty(c.ExpectedSha1))
            {
                entries.Add(MakeSkip(c, "", "",
                    $"{SkipReason.InvalidHash}: SHA1 is empty",
                    SkipReason.InvalidHash));
                Increment(skipReasonCounts, SkipReason.InvalidHash);
                invalidHash++;
                continue;
            }

            // ── Guard: already assigned to a volume ───────────────────────
            if (assignedWithVolumes.TryGetValue(c.DerivedArtifactId, out var assignedVolumeLabel))
            {
                bool isStale   = assignedVolumeLabel == "(stale assignment)";
                string key     = isStale ? SkipReason.StaleVolumeAssignment : SkipReason.AlreadyAssigned;
                string reason  = isStale
                    ? $"{SkipReason.StaleVolumeAssignment}: previously assigned to a volume that no longer exists"
                    : $"{SkipReason.AlreadyAssigned}: assigned to volume \"{assignedVolumeLabel}\"";
                entries.Add(MakeSkip(c, "", "", reason, key));
                Increment(skipReasonCounts, key);
                alreadyAssigned++;
                continue;
            }

            var archivePath = Path.Combine(archiveRoot,
                c.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetPath  = VolumeArtifactPathBuilder.GetFlatFullPath(volumeRootPath, c.FileName);

            // ── Guard: archive file missing ───────────────────────────────
            if (!File.Exists(archivePath))
            {
                entries.Add(MakeSkip(c, archivePath, targetPath,
                    $"{SkipReason.ArchiveMissing}: expected at \"{archivePath}\"",
                    SkipReason.ArchiveMissing));
                Increment(skipReasonCounts, SkipReason.ArchiveMissing);
                archiveMissing++;
                continue;
            }

            knownWantedInArchive++;
            unassignedWantedInArchive++;

            // ── Guard: target path collision ──────────────────────────────
            if (File.Exists(targetPath))
            {
                entries.Add(MakeSkip(c, archivePath, targetPath,
                    $"{SkipReason.TargetPathExists}: \"{targetPath}\" already exists on the volume",
                    SkipReason.TargetPathExists));
                Increment(skipReasonCounts, SkipReason.TargetPathExists);
                targetCollision++;
                unassignedWantedInArchive--;
                continue;
            }

            // ── Guard: too large for remaining space ──────────────────────
            if (c.SizeBytes > targetFree - allocated)
            {
                entries.Add(MakeSkip(c, archivePath, targetPath,
                    $"{SkipReason.TooLargeForRemainingTargetSpace}: needs {FormatBytes(c.SizeBytes)}, remaining {FormatBytes(targetFree - allocated)}",
                    SkipReason.TooLargeForRemainingTargetSpace));
                Increment(skipReasonCounts, SkipReason.TooLargeForRemainingTargetSpace);
                tooLarge++;
                unassignedWantedInArchive--;
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
                ReasonKey          = "",
            });
            allocated += c.SizeBytes;
        }

        int plannedCount = entries.Count(e => e.Action == AppendEntryAction.Copy);
        int skippedCount = entries.Count(e => e.Action == AppendEntryAction.Skip);

        finalAllocatedBytes = allocated;

        return new AppendVolumePlan
        {
            VolumeId              = volumeId,
            VolumeLabel           = volumeLabel,
            DatLineId             = datLineId,
            VolumeRootPath        = volumeRootPath,
            TargetCapacityBytes   = plannedSizeBytes,
            TargetUsedBytes       = actualSizeBytes,
            TargetFreeBytes       = targetFree,
            TotalDerivedArtifactsForDatLine = totalDa,
            ReleaseUnwantedSkipped  = releaseUnwanted,
            TotalCandidates         = candidates.Count,
            AlreadyAssignedSkipped  = alreadyAssigned,
            ArchiveMissingSkipped   = archiveMissing,
            TargetCollisionSkipped  = targetCollision,
            TooLargeSkipped         = tooLarge,
            InvalidHashSkipped      = invalidHash,
            ExcludedIncomingSkipPath  = incomingSkipIgnored,
            ExcludedZeroOrInvalidSize = invalidSize,
            PlannedCount             = plannedCount,
            PlannedBytes             = allocated - initialAllocatedBytes,   // bytes THIS leaf/plan contributes
            SkippedCount             = skippedCount,
            RemainingTargetFreeBytes = targetFree - allocated,             // global remaining after this plan
            TargetBytesAfter         = actualSizeBytes + allocated,
            LargestCandidateBytes    = largestCandidateBytes,
            SmallestCandidateBytes   = smallestCandidateBytes,
            TotalCandidateBytes      = totalCandidateBytes,
            ActiveArchivePhysicalFileCount         = physicalFileCount,
            ActiveArchiveKnownWantedFileCount      = knownWantedInArchive,
            ActiveArchiveUnassignedWantedFileCount = unassignedWantedInArchive,
            Entries               = entries,
            SkipReasonCounts      = skipReasonCounts,
            CanExecute            = plannedCount > 0,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppendVolumePlan Empty(
        string volumeId, string volumeLabel, string datLineId, string platformId,
        long plannedSizeBytes, long actualSizeBytes, string volumeRoot, string archiveRoot)
    {
        var archiveDir = Path.Combine(archiveRoot, "archive", platformId, datLineId);
        int physCount  = Directory.Exists(archiveDir)
            ? Directory.GetFiles(archiveDir, "*", SearchOption.AllDirectories).Length
            : 0;

        return new AppendVolumePlan
        {
            VolumeId              = volumeId,
            VolumeLabel           = volumeLabel,
            DatLineId             = datLineId,
            VolumeRootPath        = volumeRoot,
            TargetCapacityBytes   = plannedSizeBytes,
            TargetUsedBytes       = actualSizeBytes,
            TargetFreeBytes       = plannedSizeBytes - actualSizeBytes,
            TotalDerivedArtifactsForDatLine = 0,
            ReleaseUnwantedSkipped  = 0,
            TotalCandidates         = 0,
            AlreadyAssignedSkipped  = 0,
            ArchiveMissingSkipped   = 0,
            TargetCollisionSkipped  = 0,
            TooLargeSkipped         = 0,
            InvalidHashSkipped      = 0,
            ExcludedIncomingSkipPath  = 0,
            ExcludedZeroOrInvalidSize = 0,
            PlannedCount             = 0,
            PlannedBytes             = 0,
            SkippedCount             = 0,
            RemainingTargetFreeBytes = plannedSizeBytes - actualSizeBytes,
            TargetBytesAfter         = actualSizeBytes,
            LargestCandidateBytes    = 0,
            SmallestCandidateBytes   = 0,
            TotalCandidateBytes      = 0,
            ActiveArchivePhysicalFileCount         = physCount,
            ActiveArchiveKnownWantedFileCount      = 0,
            ActiveArchiveUnassignedWantedFileCount = 0,
            Entries               = [],
            SkipReasonCounts      = new Dictionary<string, int>(),
            CanExecute            = false,
        };
    }

    private static AppendEntry MakeSkip(AppendCandidateInfo c,
        string archivePath, string targetPath, string reason, string reasonKey) => new()
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
        ReasonKey          = reasonKey,
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
