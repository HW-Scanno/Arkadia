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

    public AppendVolumeService(CatalogService catalog) => _catalog = catalog;

    public AppendVolumeResult Execute(
        AppendVolumePlan                 plan,
        IProgress<AppendVolumeProgress>? progress = null)
    {
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
