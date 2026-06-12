using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Executes a <see cref="VolumeFillbackPlan"/> produced by <see cref="VolumeFillbackPlanner"/>.
///
/// Safety invariants enforced:
///   • Never overwrites an existing target file.
///   • Never deletes source before target is verified (cross-disk).
///   • Never updates DB before the physical operation is confirmed.
///   • Per-artifact DB update is transactional via
///     <see cref="CatalogService.MoveVolumeArtifactToVolume"/>.
/// </summary>
public sealed class VolumeFillbackService
{
    private readonly CatalogService _catalog;

    public VolumeFillbackService(CatalogService catalog) => _catalog = catalog;

    // ── Public API ────────────────────────────────────────────────────────────

    public VolumeFillbackResult Execute(
        VolumeFillbackPlan            plan,
        DatLineStore                  store,
        IProgress<FillbackProgress>?  progress = null)
    {
        var log    = new List<string>();
        var result = new VolumeFillbackResult { LogLines = log };

        var activeEntries = plan.Entries
            .Where(e => e.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete)
            .ToList();

        foreach (var entry in activeEntries)
        {
            bool ok = entry.Action == FillbackEntryAction.Move
                ? ExecuteMove(entry, plan, progress, log, result)
                : ExecuteCopyVerifyDelete(entry, plan, progress, log, result);

            if (!ok && entry.Action == FillbackEntryAction.Move)
            {
                // Same-disk move failed — log critical context and stop
                log.Add($"CRITICAL  move-failed  {entry.ArtifactFileName}  db-not-updated");
            }
        }

        // Emit skips
        foreach (var entry in plan.Entries.Where(e => e.Action == FillbackEntryAction.Skip))
        {
            progress?.Report(new FillbackProgress("fillback-skip", entry.ArtifactFileName, entry.Reason));
            log.Add($"fillback-skip  {entry.ArtifactFileName}  {entry.Reason}");
        }

        // Check source empty
        var remainingVAs = _catalog.GetVolumeArtifacts(plan.SourceVolumeId)
            .Where(va => va.Status == "present_in_final")
            .ToList();
        result.SourceEmpty = remainingVAs.Count == 0;

        progress?.Report(new FillbackProgress("usage-refreshed",
            $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}", ""));

        log.Add($"fillback-summary  moved={result.MovedCount}  copied={result.CopiedCount}" +
                $"  bytes={result.BytesMoved}  errors={result.ErrorCount}" +
                $"  source-empty={result.SourceEmpty}");

        return result;
    }

    // ── Same-disk move ────────────────────────────────────────────────────────

    private bool ExecuteMove(
        FillbackEntry            entry,
        VolumeFillbackPlan       plan,
        IProgress<FillbackProgress>? progress,
        List<string>             log,
        VolumeFillbackResult     result)
    {
        progress?.Report(new FillbackProgress("fillback-moving", entry.ArtifactFileName,
            $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}"));
        log.Add($"fillback-moving  {entry.ArtifactFileName}");

        // Source must still exist
        if (!File.Exists(entry.SourceFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  source-missing-before-move");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "source not found"));
            result.ErrorCount++;
            return false;
        }

        // Target must not exist
        if (File.Exists(entry.TargetFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  target-already-exists");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "target already exists"));
            result.ErrorCount++;
            return false;
        }

        // Move (overwrite:false is the default for File.Move with new overload, use false explicitly)
        try
        {
            File.Move(entry.SourceFullPath, entry.TargetFullPath, overwrite: false);
        }
        catch (Exception ex)
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  move-failed  {ex.Message}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, ex.Message));
            result.ErrorCount++;
            return false;
        }

        // Confirm target exists and source is gone
        if (!File.Exists(entry.TargetFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  target-missing-after-move");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "target missing after move"));
            result.ErrorCount++;
            return false;
        }
        if (File.Exists(entry.SourceFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  source-still-exists-after-move");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "source still exists after move"));
            result.ErrorCount++;
            return false;
        }

        // Verify target hash
        progress?.Report(new FillbackProgress("fillback-verifying", entry.ArtifactFileName,
            $"sha1={Short(entry.ExpectedSha1)}…"));

        if (!AppendVerifier.VerifyDestination(
            entry.TargetFullPath, entry.SizeBytes, entry.ExpectedSha1,
            out var failReason, out var verifyLog))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  hash-mismatch  {verifyLog.Trim()}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, failReason ?? "hash mismatch"));
            result.ErrorCount++;
            // Target has wrong hash but source is gone — log critical state
            log.Add($"CRITICAL  same-disk-move-hash-failed  {entry.ArtifactFileName}  " +
                    $"target={entry.TargetFullPath}  source-gone");
            return false;
        }

        // Update DB — only after physical success
        _catalog.MoveVolumeArtifactToVolume(
            entry.VolumeArtifactId, plan.SourceVolumeId, plan.TargetVolumeId, entry.SizeBytes);

        log.Add($"fillback-moved  {entry.ArtifactFileName}  " +
                $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}  sha1={Short(entry.ExpectedSha1)}");
        progress?.Report(new FillbackProgress("fillback-moved", entry.ArtifactFileName,
            $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}"));

        result.MovedCount++;
        result.BytesMoved += entry.SizeBytes;
        return true;
    }

    // ── Cross-disk copy → verify → delete ────────────────────────────────────

    private bool ExecuteCopyVerifyDelete(
        FillbackEntry            entry,
        VolumeFillbackPlan       plan,
        IProgress<FillbackProgress>? progress,
        List<string>             log,
        VolumeFillbackResult     result)
    {
        progress?.Report(new FillbackProgress("fillback-copying", entry.ArtifactFileName,
            $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}"));
        log.Add($"fillback-copying  {entry.ArtifactFileName}");

        // Source must still exist
        if (!File.Exists(entry.SourceFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  source-missing-before-copy");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "source not found"));
            result.ErrorCount++;
            return false;
        }

        // Target must not exist
        if (File.Exists(entry.TargetFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  target-already-exists");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "target already exists"));
            result.ErrorCount++;
            return false;
        }

        // Copy
        bool copyCreatedTarget = false;
        try
        {
            File.Copy(entry.SourceFullPath, entry.TargetFullPath, overwrite: false);
            copyCreatedTarget = true;
        }
        catch (Exception ex)
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  copy-failed  {ex.Message}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, ex.Message));
            result.ErrorCount++;
            return false;
        }

        // Confirm target exists
        if (!File.Exists(entry.TargetFullPath))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  target-missing-after-copy");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "target missing after copy"));
            result.ErrorCount++;
            return false;
        }

        // Verify target hash — never delete source before this passes
        progress?.Report(new FillbackProgress("fillback-verifying", entry.ArtifactFileName,
            $"sha1={Short(entry.ExpectedSha1)}…"));

        if (!AppendVerifier.VerifyDestination(
            entry.TargetFullPath, entry.SizeBytes, entry.ExpectedSha1,
            out var failReason, out var verifyLog))
        {
            log.Add($"fillback-error  {entry.ArtifactFileName}  hash-mismatch  {verifyLog.Trim()}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, failReason ?? "hash mismatch"));

            // Delete bad target (it was created by this operation, so it's safe to remove)
            if (copyCreatedTarget)
            {
                try
                {
                    File.Delete(entry.TargetFullPath);
                    log.Add($"cleanup  deleted-bad-target  {entry.ArtifactFileName}");
                }
                catch (Exception delEx)
                {
                    log.Add($"cleanup-failed  {entry.ArtifactFileName}  {delEx.Message}");
                }
            }

            result.ErrorCount++;
            return false;
        }

        // Delete source — only after verified target
        progress?.Report(new FillbackProgress("fillback-deleting-source", entry.ArtifactFileName, ""));
        log.Add($"fillback-deleting-source  {entry.ArtifactFileName}");

        try
        {
            File.Delete(entry.SourceFullPath);
        }
        catch (Exception ex)
        {
            // Source delete failed — target is valid but DB still points to source.
            // Log critical state; do not update DB or continue silently.
            log.Add($"CRITICAL  source-delete-failed  {entry.ArtifactFileName}  " +
                    $"target={entry.TargetFullPath}  source={entry.SourceFullPath}  {ex.Message}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName,
                $"source delete failed: {ex.Message}"));
            result.ErrorCount++;
            return false;
        }

        // Confirm source gone
        if (File.Exists(entry.SourceFullPath))
        {
            log.Add($"CRITICAL  source-still-exists-after-delete  {entry.ArtifactFileName}");
            progress?.Report(new FillbackProgress("fillback-error", entry.ArtifactFileName, "source still exists after delete"));
            result.ErrorCount++;
            return false;
        }

        // Update DB — only after full physical success (copy + verify + delete)
        _catalog.MoveVolumeArtifactToVolume(
            entry.VolumeArtifactId, plan.SourceVolumeId, plan.TargetVolumeId, entry.SizeBytes);

        log.Add($"fillback-copied-verified-deleted  {entry.ArtifactFileName}  " +
                $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}  sha1={Short(entry.ExpectedSha1)}");
        progress?.Report(new FillbackProgress("fillback-copied-verified-deleted", entry.ArtifactFileName,
            $"{plan.SourceVolumeLabel} → {plan.TargetVolumeLabel}"));

        result.CopiedCount++;
        result.BytesMoved += entry.SizeBytes;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Short(string sha1)
        => sha1.Length >= 8 ? sha1[..8] : sha1;
}
