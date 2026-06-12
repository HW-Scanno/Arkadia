using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Dry-run planner for the Fillback operation.
///
/// Reads from catalog + DAT-line store and builds a <see cref="VolumeFillbackPlan"/>
/// without touching any files.  The caller is responsible for resolving physical
/// root paths before calling <see cref="Plan"/>.
/// </summary>
public sealed class VolumeFillbackPlanner
{
    private readonly CatalogService _catalog;

    public VolumeFillbackPlanner(CatalogService catalog) => _catalog = catalog;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fillback plan from <paramref name="source"/> into <paramref name="target"/>.
    /// Both root paths must be accessible directory paths (callers must resolve them
    /// from the disk / workspace before calling this method).
    ///
    /// Returns a plan with <see cref="VolumeFillbackPlan.CanExecute"/> = false
    /// when any hard precondition fails.
    /// </summary>
    public VolumeFillbackPlan Plan(
        VolumeRecord source,
        VolumeRecord target,
        string       sourceRootPath,
        string       targetRootPath,
        string       sourceDiskLabel,
        string       targetDiskLabel,
        DatLineStore store)
    {
        var issues   = new List<string>();
        var warnings = new List<string>();

        // ── Hard preconditions ────────────────────────────────────────────────
        if (source.Id == target.Id)
        {
            issues.Add("Source and target volumes must be different.");
            return CannotExecute(source, target, sourceRootPath, targetRootPath,
                sourceDiskLabel, targetDiskLabel, issues, warnings);
        }
        if (source.DatLineId != target.DatLineId)
        {
            issues.Add("Source and target volumes must belong to the same DAT line.");
            return CannotExecute(source, target, sourceRootPath, targetRootPath,
                sourceDiskLabel, targetDiskLabel, issues, warnings);
        }
        if (!Directory.Exists(sourceRootPath))
        {
            issues.Add("Source volume disk is not mounted.");
            return CannotExecute(source, target, sourceRootPath, targetRootPath,
                sourceDiskLabel, targetDiskLabel, issues, warnings);
        }
        if (!Directory.Exists(targetRootPath))
        {
            issues.Add("Target volume disk is not mounted.");
            return CannotExecute(source, target, sourceRootPath, targetRootPath,
                sourceDiskLabel, targetDiskLabel, issues, warnings);
        }

        // ── Operation mode ────────────────────────────────────────────────────
        var mode = IsSameDisk(sourceRootPath, targetRootPath)
            ? FillbackOperationMode.MoveSameDisk
            : FillbackOperationMode.CopyVerifyDeleteCrossDisk;

        // ── Source artifact candidates ────────────────────────────────────────
        var sourceVAs = _catalog.GetVolumeArtifacts(source.Id)
            .Where(va => va.Status == "present_in_final")
            .ToList();

        if (sourceVAs.Count == 0)
        {
            issues.Add("Source volume has no active artifacts.");
            return CannotExecute(source, target, sourceRootPath, targetRootPath,
                sourceDiskLabel, targetDiskLabel, issues, warnings);
        }

        var daIds       = sourceVAs.Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetFillbackCandidateInfos(daIds);  // excludes unwanted
        var vaByDaId    = sourceVAs.ToDictionary(va => va.DerivedArtifactId, StringComparer.Ordinal);

        // ── Build entries alphabetically ──────────────────────────────────────
        long targetFree = target.PlannedSizeBytes - target.ActualSizeBytes;
        long allocated  = 0;
        int  skipped    = 0;

        var entries         = new List<FillbackEntry>();
        var skipReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var vi in verifyInfos
            .OrderBy(v => v.ReleaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.FileName,    StringComparer.OrdinalIgnoreCase))
        {
            if (!vaByDaId.TryGetValue(vi.DerivedArtifactId, out var va))
                continue;

            var src = VolumeArtifactPathBuilder.GetFlatFullPath(sourceRootPath, vi.FileName);
            var dst = VolumeArtifactPathBuilder.GetFlatFullPath(targetRootPath, vi.FileName);

            // Source must physically exist (flat path only)
            if (!File.Exists(src))
            {
                var r = SkipReason.SourceFileMissing;
                entries.Add(Skip(va, vi, src, dst, r));
                skipReasonCounts[r] = skipReasonCounts.GetValueOrDefault(r) + 1;
                skipped++;
                continue;
            }

            // Collision check
            if (File.Exists(dst))
            {
                bool alreadyOnTarget = _catalog.VolumeArtifactExists(target.Id, vi.DerivedArtifactId);
                if (alreadyOnTarget)
                {
                    var r = SkipReason.AlreadyOnTarget;
                    entries.Add(Skip(va, vi, src, dst, r));
                    skipReasonCounts[r] = skipReasonCounts.GetValueOrDefault(r) + 1;
                    skipped++;
                }
                else
                {
                    entries.Add(Error(va, vi, src, dst,
                        $"{SkipReason.TargetCollision}: {vi.FileName} already exists at target with unknown content"));
                }
                continue;
            }

            // Capacity check — skip too-large, but continue so smaller artifacts can fill gaps
            if (vi.SizeBytes > targetFree - allocated)
            {
                var r = $"{SkipReason.TooLargeForRemainingTargetSpace}: needs {FormatBytes(vi.SizeBytes)}, remaining {FormatBytes(targetFree - allocated)}";
                entries.Add(Skip(va, vi, src, dst, r));
                var code = SkipReason.TooLargeForRemainingTargetSpace;
                skipReasonCounts[code] = skipReasonCounts.GetValueOrDefault(code) + 1;
                skipped++;
                continue;
            }

            // Plan the operation
            var action = mode == FillbackOperationMode.MoveSameDisk
                ? FillbackEntryAction.Move
                : FillbackEntryAction.CopyVerifyDelete;

            entries.Add(new FillbackEntry
            {
                VolumeArtifactId  = va.Id,
                DerivedArtifactId = vi.DerivedArtifactId,
                ReleaseName       = vi.ReleaseName,
                ArtifactFileName  = vi.FileName,
                SizeBytes         = vi.SizeBytes,
                ExpectedSha1      = vi.Sha1,
                SourceFullPath    = src,
                TargetFullPath    = dst,
                Action            = action,
                Reason            = "",
            });
            allocated += vi.SizeBytes;
        }

        var plannedCount  = entries.Count(e => e.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete);
        var errorEntries  = entries.Where(e => e.Action == FillbackEntryAction.Error).ToList();

        if (errorEntries.Count > 0)
            warnings.Add($"{errorEntries.Count} collision(s) detected — resolve before executing.");

        return new VolumeFillbackPlan
        {
            SourceVolumeId           = source.Id,
            SourceVolumeLabel        = source.Label,
            SourceDiskLabel          = sourceDiskLabel,
            SourceRootPath           = sourceRootPath,
            TargetVolumeId           = target.Id,
            TargetVolumeLabel        = target.Label,
            TargetDiskLabel          = targetDiskLabel,
            TargetRootPath           = targetRootPath,
            OperationMode            = mode,
            TargetCapacityBytes      = target.PlannedSizeBytes,
            TargetUsedBytes          = target.ActualSizeBytes,
            TargetFreeBytes          = targetFree,
            PlannedBytes             = allocated,
            PlannedCount             = plannedCount,
            SkippedCount             = skipped,
            RemainingTargetFreeBytes = targetFree - allocated,
            SourceBytesBefore        = source.ActualSizeBytes,
            SourceBytesAfter         = source.ActualSizeBytes - allocated,
            TargetBytesAfter         = target.ActualSizeBytes + allocated,
            Entries                  = entries,
            Warnings                 = warnings,
            Issues                   = errorEntries.Select(e => e.Reason).ToList(),
            SkipReasonCounts         = skipReasonCounts,
            CanExecute               = plannedCount > 0 && errorEntries.Count == 0,
        };
    }

    // ── Skip-reason codes ─────────────────────────────────────────────────────

    /// <summary>Machine-readable skip-reason codes surfaced in the plan and UI.</summary>
    public static class SkipReason
    {
        public const string SourceFileMissing               = "SourceFileMissing";
        public const string TooLargeForRemainingTargetSpace = "TooLargeForRemainingTargetSpace";
        public const string AlreadyOnTarget                 = "AlreadyOnTarget";
        public const string TargetCollision                 = "TargetCollision";
    }

    /// <summary>Extracts the reason code (text before the first ':').</summary>
    private static string ReasonCode(string reason)
    {
        var idx = reason.IndexOf(':');
        return idx < 0 ? reason : reason[..idx];
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the filesystem roots of two paths.
    /// On Windows, comparing drive roots (e.g. "D:\") is sufficient.
    /// When uncertain, falls back to cross-disk mode (safer).
    /// </summary>
    internal static bool IsSameDisk(string pathA, string pathB)
    {
        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);
        return rootA is not null
            && rootB is not null
            && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static FillbackEntry Skip(VolumeArtifactRecord va, ArtifactVerifyInfo vi,
        string src, string dst, string reason) =>
        new()
        {
            VolumeArtifactId  = va.Id,
            DerivedArtifactId = vi.DerivedArtifactId,
            ReleaseName       = vi.ReleaseName,
            ArtifactFileName  = vi.FileName,
            SizeBytes         = vi.SizeBytes,
            ExpectedSha1      = vi.Sha1,
            SourceFullPath    = src,
            TargetFullPath    = dst,
            Action            = FillbackEntryAction.Skip,
            Reason            = reason,
        };

    private static FillbackEntry Error(VolumeArtifactRecord va, ArtifactVerifyInfo vi,
        string src, string dst, string reason) =>
        new()
        {
            VolumeArtifactId  = va.Id,
            DerivedArtifactId = vi.DerivedArtifactId,
            ReleaseName       = vi.ReleaseName,
            ArtifactFileName  = vi.FileName,
            SizeBytes         = vi.SizeBytes,
            ExpectedSha1      = vi.Sha1,
            SourceFullPath    = src,
            TargetFullPath    = dst,
            Action            = FillbackEntryAction.Error,
            Reason            = reason,
        };

    private static VolumeFillbackPlan CannotExecute(
        VolumeRecord source, VolumeRecord target,
        string sourceRoot, string targetRoot,
        string sourceDiskLabel, string targetDiskLabel,
        List<string> issues, List<string> warnings) =>
        new()
        {
            SourceVolumeId           = source.Id,
            SourceVolumeLabel        = source.Label,
            SourceDiskLabel          = sourceDiskLabel,
            SourceRootPath           = sourceRoot,
            TargetVolumeId           = target.Id,
            TargetVolumeLabel        = target.Label,
            TargetDiskLabel          = targetDiskLabel,
            TargetRootPath           = targetRoot,
            OperationMode            = FillbackOperationMode.CopyVerifyDeleteCrossDisk,
            TargetCapacityBytes      = target.PlannedSizeBytes,
            TargetUsedBytes          = target.ActualSizeBytes,
            TargetFreeBytes          = target.PlannedSizeBytes - target.ActualSizeBytes,
            PlannedBytes             = 0,
            PlannedCount             = 0,
            SkippedCount             = 0,
            RemainingTargetFreeBytes = target.PlannedSizeBytes - target.ActualSizeBytes,
            SourceBytesBefore        = source.ActualSizeBytes,
            SourceBytesAfter         = source.ActualSizeBytes,
            TargetBytesAfter         = target.ActualSizeBytes,
            Entries                  = [],
            Warnings                 = warnings,
            Issues                   = issues,
            SkipReasonCounts         = new Dictionary<string, int>(StringComparer.Ordinal),
            CanExecute               = false,
        };
}
