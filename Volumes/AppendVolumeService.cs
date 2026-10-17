using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Execution service for the Append Volume operation.
///
/// For each planned entry: copies the archive artifact to the volume, verifies the SHA1,
/// commits the volume_artifact DB row, then deletes the local archive source.
/// The source is only deleted after the target is verified and the DB row is committed.
/// </summary>
public sealed class AppendVolumeService
{
    private readonly CatalogService _catalog;
    private readonly Arkadia.Activities.ActivityMonitor? _activities;

    /// <param name="activities">Shared activity monitor (null = not observed). It only observes execution.</param>
    public AppendVolumeService(CatalogService catalog, Arkadia.Activities.ActivityMonitor? activities = null)
    {
        _catalog = catalog;
        _activities = activities;
    }

    /// <summary>
    /// Group execution: runs each non-empty leaf plan through <see cref="Execute"/>, sequentially; results are summed.
    /// One APPENDING scope covers the whole run, so the status never drops to READY between leaves. A per-entry
    /// failure follows the single-Append result semantics; an unexpected exception propagates (stopping further
    /// leaves) with earlier leaves' committed work retained (no rollback).
    /// </summary>
    public AppendVolumeResult ExecuteGroup(GroupAppendVolumePlan groupPlan, IProgress<AppendVolumeProgress> progress)
    {
        using var activity = _activities?.Begin(Arkadia.Activities.ActivityKind.Appending, "append Group volume");
        int  copied = 0, errors = 0, deleted = 0, deleteFailed = 0;
        long bytes  = 0;
        var  logs   = new List<string>();

        foreach (var leafPlan in groupPlan.LeafPlans)   // DatLineId Ordinal (planning order)
        {
            if (leafPlan.PlannedCount == 0) continue;
            var r = Execute(leafPlan, progress);
            copied       += r.CopiedCount;
            bytes        += r.BytesCopied;
            errors       += r.ErrorCount;
            deleted      += r.SourcesDeletedCount;
            deleteFailed += r.SourceDeleteFailedCount;
            logs.AddRange(r.LogLines);
        }

        return new AppendVolumeResult
        {
            CopiedCount             = copied,
            BytesCopied             = bytes,
            ErrorCount              = errors,
            SourcesDeletedCount     = deleted,
            SourceDeleteFailedCount = deleteFailed,
            LogLines                = logs,
        };
    }

    public AppendVolumeResult Execute(
        AppendVolumePlan                 plan,
        IProgress<AppendVolumeProgress>? progress = null)
    {
        using var activity = _activities?.Begin(Arkadia.Activities.ActivityKind.Appending, "append volume");
        int  copiedCount             = 0;
        long bytesCopied             = 0;
        int  errorCount              = 0;
        int  sourcesDeletedCount     = 0;
        int  sourceDeleteFailedCount = 0;
        var  logLines                = new List<string>();

        foreach (var entry in plan.Entries)
        {
            if (entry.Action != AppendEntryAction.Copy) continue;

            progress?.Report(new AppendVolumeProgress("append-copying", entry.FileName,
                FormatBytes(entry.SizeBytes)));

            // Copy
            try
            {
                // Ensure the target's parent exists. For Single flat volumes the parent is the volume root
                // (already present — a no-op); for Group nested layout it is the leaf-segment folder, which may
                // not exist yet. Only the planned target's own parent is created — never an arbitrary path.
                var targetDir = Path.GetDirectoryName(entry.TargetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                File.Copy(entry.ArchivePath, entry.TargetPath, overwrite: false);
            }
            catch (Exception ex)
            {
                logLines.Add($"COPY-FAIL  {entry.FileName}  {ex.Message}");
                errorCount++;
                progress?.Report(new AppendVolumeProgress("append-error", entry.FileName,
                    ex.Message));
                continue;
            }

            // Verify destination
            var ok = AppendVerifier.VerifyDestination(
                entry.TargetPath, entry.SizeBytes, entry.ExpectedSha1,
                out var failReason, out var verifyLog);
            logLines.Add(verifyLog);

            if (!ok)
            {
                try { File.Delete(entry.TargetPath); } catch { }
                logLines.Add($"APPEND-VERIFY-FAIL  {entry.FileName}  {failReason}");
                errorCount++;
                progress?.Report(new AppendVolumeProgress("append-error", entry.FileName,
                    failReason ?? "verify failed"));
                continue;
            }

            // Only after verification: create VA row + increment volume actual_size_bytes
            var va = new VolumeArtifactRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                VolumeId           = plan.VolumeId,
                DatLineId          = plan.DatLineId,
                DerivedArtifactId  = entry.DerivedArtifactId,
                ContentIdentityKey = entry.ContentIdentityKey,
                Status             = "present_in_final",
                AddedAtUtc         = DateTime.UtcNow,
            };

            try
            {
                _catalog.AddVolumeArtifactAndIncrementSize(va, entry.SizeBytes);
            }
            catch (Exception ex)
            {
                logLines.Add($"APPEND-DB-FAIL  {entry.FileName}  {ex.Message}");
                errorCount++;
                progress?.Report(new AppendVolumeProgress("append-error", entry.FileName,
                    ex.Message));
                continue;
            }

            copiedCount++;
            bytesCopied += entry.SizeBytes;
            logLines.Add($"APPEND-OK  {entry.FileName}");

            progress?.Report(new AppendVolumeProgress("append-copied", entry.FileName,
                FormatBytes(entry.SizeBytes)));

            // Delete archive source — safe: target verified, DB row committed
            try
            {
                File.Delete(entry.ArchivePath);
                sourcesDeletedCount++;
                logLines.Add($"APPEND-SOURCE-DELETED  {entry.FileName}");
                progress?.Report(new AppendVolumeProgress("append-source-deleted", entry.FileName,
                    entry.ArchivePath));
            }
            catch (Exception ex)
            {
                sourceDeleteFailedCount++;
                logLines.Add($"APPEND-SOURCE-DELETE-FAILED  {entry.FileName}  {ex.Message}");
                progress?.Report(new AppendVolumeProgress("append-source-delete-failed", entry.FileName,
                    ex.Message));
            }
        }

        return new AppendVolumeResult
        {
            CopiedCount             = copiedCount,
            BytesCopied             = bytesCopied,
            ErrorCount              = errorCount,
            SourcesDeletedCount     = sourcesDeletedCount,
            SourceDeleteFailedCount = sourceDeleteFailedCount,
            LogLines                = logLines,
        };
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
