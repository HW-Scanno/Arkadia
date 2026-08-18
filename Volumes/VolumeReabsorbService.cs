using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>Live progress event during Reabsorb (one row per file action).</summary>
public sealed record ReabsorbProgress(string Action, string FileName, string Detail);

/// <summary>Outcome of a Reabsorb execution.</summary>
public sealed class VolumeReabsorbResult
{
    public int                  RecoveredCount { get; init; }
    public long                 CopiedBytes    { get; init; }
    public long                 VerifiedBytes  { get; init; }
    public int                  TotalFiles     { get; init; }
    public string?              AbortReason    { get; init; }
    public bool                 CleanupFailed  { get; init; }
    public bool                 FullSuccess    { get; init; }
    public bool                 VolumeDeleted  { get; init; }
    public IReadOnlyList<string> LogLines      { get; init; } = [];
}

/// <summary>
/// The extracted Reabsorb execution core (V-G8) — the SAME historical Single loop, made reusable for Single and
/// Group. For each <see cref="ReabsorbCandidate"/> it runs the unchanged copy→verify→delete sequence
/// (CASE A: adopt a matching archive copy; CASE B: copy volume→archive, verify SOURCE-vs-DESTINATION SHA1, delete
/// the volume copy only after verification), finalizes DA/release state on the correct <b>leaf</b> store(s), and
/// applies the historical lifecycle: full success → delete the physical volume root + <c>DeleteVolume</c>; partial
/// → <c>RemoveVolumeArtifacts</c> for the transferred rows (the pre-existing partial <c>actual_size</c> quirk is
/// intentionally NOT changed). No DB-SHA1 redesign, no File.Move, no new archive layout. The archive destination is
/// the DB-authoritative <see cref="ReabsorbCandidate.ArchiveDestPath"/>. Progress is reported; the caller drives UI.
/// </summary>
public sealed class VolumeReabsorbService
{
    private readonly CatalogService _catalog;

    public VolumeReabsorbService(CatalogService catalog) => _catalog = catalog;

    public VolumeReabsorbResult Execute(
        string                           volumeId,
        string                           volumeRoot,
        IReadOnlyList<ReabsorbCandidate> candidates,
        IProgress<ReabsorbProgress>?     progress = null)
    {
        var log           = new List<string>();
        string? abortReason = null;
        bool cleanupFailed  = false;
        long copiedBytes = 0, verifiedBytes = 0;
        var recovered     = new List<string>();

        // ── Per-artifact: copy → verify → delete (strict per-file order) ──
        // An unexpected exception is caught and treated as an abort (matching the historical handler): prior
        // successes still finalize below and the partial lifecycle applies.
        try
        {
        foreach (var c in candidates)
        {
            var sizeLabel = FormatBytes(c.SizeBytes);
            var src = c.SourcePath;
            var dst = c.ArchiveDestPath;

            if (File.Exists(dst))
            {
                // ── CASE A: artifact already present in Local Archive ──
                log.Add($"already-present-local  {c.FileName}  ({sizeLabel})");
                progress?.Report(new ReabsorbProgress("already-local", c.FileName, sizeLabel));

                var sha1Dst = Sha1(dst);
                var sha1Src = Sha1(src);

                if (string.Equals(sha1Dst, sha1Src, StringComparison.OrdinalIgnoreCase))
                {
                    log.Add($"verify-ok  {c.FileName}  sha1={sha1Dst}");
                    recovered.Add(c.DerivedArtifactId);
                    try { File.Delete(src); log.Add($"delete-from-volume  {c.FileName}"); }
                    catch (Exception ex) { log.Add($"delete-from-volume-failed  {c.FileName}  {ex.Message}"); cleanupFailed = true; break; }
                    verifiedBytes += c.SizeBytes;
                    progress?.Report(new ReabsorbProgress("verify-ok", c.FileName, sizeLabel));
                    continue;
                }

                log.Add($"local-invalid  {c.FileName}  sha1-local={sha1Dst}  sha1-vol={sha1Src}  — re-copying");
                // fall through to CASE B (overwrite)
            }

            // ── CASE B: copy from volume → Local Archive ──
            log.Add($"copy-start  {c.FileName}  ({sizeLabel})");
            progress?.Report(new ReabsorbProgress("copy-start", c.FileName, sizeLabel));

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            copiedBytes += c.SizeBytes;

            var sS = Sha1(src);
            var sD = Sha1(dst);
            if (!string.Equals(sS, sD, StringComparison.OrdinalIgnoreCase))
            {
                log.Add($"verify-failed  {c.FileName}  sha1-src={sS}  sha1-dst={sD}");
                abortReason = $"SHA1 mismatch: {c.FileName}";
                progress?.Report(new ReabsorbProgress("verify-failed", c.FileName, sizeLabel));
                break;   // abort — leave source intact
            }

            log.Add($"verify-ok  {c.FileName}  sha1={sD}");
            verifiedBytes += c.SizeBytes;
            recovered.Add(c.DerivedArtifactId);
            try { File.Delete(src); log.Add($"delete-from-volume  {c.FileName}"); }
            catch (Exception ex) { log.Add($"delete-from-volume-failed  {c.FileName}  {ex.Message}"); cleanupFailed = true; break; }
            progress?.Report(new ReabsorbProgress("verify-ok", c.FileName, sizeLabel));
        }
        }
        catch (Exception ex) { abortReason = ex.Message; }

        bool fullSuccess = abortReason is null && !cleanupFailed;

        // ── DB finalization — DA present + release recalc on the correct leaf store(s) ──
        if (recovered.Count > 0)
        {
            var recoveredSet = recovered.ToHashSet(StringComparer.Ordinal);
            foreach (var g in candidates
                         .Where(x => recoveredSet.Contains(x.DerivedArtifactId))
                         .GroupBy(x => x.LeafDbAbsPath, StringComparer.OrdinalIgnoreCase))
            {
                var leafStore = new DatLineStore(g.Key);
                var ids       = g.Select(x => x.DerivedArtifactId).Distinct(StringComparer.Ordinal).ToList();
                leafStore.BatchUpdateDerivedArtifactStatus(ids, "present");
                leafStore.RecalculateReleaseStatusForArtifacts(ids);
            }
        }

        // ── Lifecycle ──
        bool volumeDeleted = false;
        if (fullSuccess)
        {
            try
            {
                if (Directory.Exists(volumeRoot))
                {
                    foreach (var dir in Directory.GetDirectories(volumeRoot, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    if (!Directory.EnumerateFileSystemEntries(volumeRoot).Any())
                        Directory.Delete(volumeRoot);
                }
            }
            catch { /* non-fatal — dir cleanup failed; DB cleanup proceeds */ }

            _catalog.DeleteVolume(volumeId);   // volume record + volume_artifacts + volume_locations only
            volumeDeleted = true;
        }
        else if (recovered.Count > 0)
        {
            // Partial: remove transferred assignments; retain the volume record.
            _catalog.RemoveVolumeArtifacts(volumeId, recovered);
        }

        return new VolumeReabsorbResult
        {
            RecoveredCount = recovered.Count,
            CopiedBytes    = copiedBytes,
            VerifiedBytes  = verifiedBytes,
            TotalFiles     = candidates.Count,
            AbortReason    = abortReason,
            CleanupFailed  = cleanupFailed,
            FullSuccess    = fullSuccess,
            VolumeDeleted  = volumeDeleted,
            LogLines       = log,
        };
    }

    private static string Sha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
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
