using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;

namespace Arkadia.LocalArchive;

/// <summary>
/// Filesystem-first scan of the active local archive for a single DAT line.
///
/// Semantics: enumerate every physical file in archive\&lt;platformId&gt;\&lt;datLineId&gt;\,
/// hash it, classify it against the DB, and report results.
/// DB artifacts absent from disk appear only in AbsentFromArchiveCount — not in
/// the primary scan entries.
/// </summary>
public sealed class LocalArchiveVerifyService
{
    private readonly string _appRoot;

    public LocalArchiveVerifyService(string appRoot) => _appRoot = appRoot;

    // ── Verify ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the archive directory for <paramref name="datLineId"/> and classifies
    /// every physical file found. Progress events are dispatched via <paramref name="progress"/>.
    /// </summary>
    /// <param name="assignedVolumes">
    /// Optional map of DerivedArtifactId → <see cref="AssignedVolumeInfo"/> built by the caller
    /// from CatalogService + VolumePathResolver. When provided, wanted artifacts already on a
    /// reachable volume are classified as <see cref="LocalArchiveClass.RedundantArchiveCopy"/>
    /// (repairable) or <see cref="LocalArchiveClass.AssignedVolumeUnavailable"/> (warning).
    /// </param>
    public LocalArchiveVerifyPlan Verify(
        string                                           platformId,
        string                                           datLineId,
        DatLineStore                                     store,
        IProgress<LocalArchiveVerifyProgress>?           progress        = null,
        IReadOnlyDictionary<string, AssignedVolumeInfo>? assignedVolumes = null)
    {
        var archiveDir = Path.Combine(_appRoot, "archive", platformId, datLineId);
        var entries    = new List<LocalArchiveEntry>();

        // Load all DB artifacts (annotated with IsUnwanted).
        var dbArtifacts = store.GetAllArchiveArtifactInfos();

        // Build SHA1 index (hash → artifact). UNWANTED WINS on collision.
        var dbBySha1 = new Dictionary<string, DatLineStore.ArchiveArtifactInfo>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var a in dbArtifacts)
        {
            if (string.IsNullOrEmpty(a.ExpectedSha1)) continue;
            if (!dbBySha1.TryGetValue(a.ExpectedSha1, out var existing))
                dbBySha1[a.ExpectedSha1] = a;
            else if (a.IsUnwanted && !existing.IsUnwanted)
                dbBySha1[a.ExpectedSha1] = a;
        }

        // Build filename index (filename → artifact), for hash-mismatch detection.
        var dbByFileName = new Dictionary<string, DatLineStore.ArchiveArtifactInfo>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var a in dbArtifacts)
        {
            if (!dbByFileName.TryGetValue(a.FileName, out var existing))
                dbByFileName[a.FileName] = a;
            else if (a.IsUnwanted && !existing.IsUnwanted)
                dbByFileName[a.FileName] = a;
        }

        // Detect duplicate filenames in DB (collision guard).
        var duplicateFileNames = dbArtifacts
            .GroupBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Enumerate physical files.
        var physFiles = new List<string>();
        if (Directory.Exists(archiveDir))
            foreach (var f in Directory.EnumerateFiles(archiveDir, "*", SearchOption.AllDirectories))
                physFiles.Add(f);

        var matchedDaIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fullPath in physFiles)
        {
            var fileName = Path.GetFileName(fullPath);
            var relPath  = Path.GetRelativePath(archiveDir, fullPath);

            progress?.Report(new("archive-found-file", fileName, relPath));

            // DB duplicate-filename collision.
            if (duplicateFileNames.Contains(fileName))
            {
                entries.Add(new LocalArchiveEntry
                {
                    Classification = LocalArchiveClass.ArchiveDuplicateCollision,
                    FileName       = fileName,
                    RelativePath   = relPath,
                    IsRepairable   = false,
                    Note           = "Multiple DB rows reference this archive filename.",
                });
                progress?.Report(new("archive-collision", fileName, "Multiple DB rows"));
                continue;
            }

            // Hash the physical file.
            progress?.Report(new("archive-hashing", fileName, ""));
            var actualSha1 = ComputeSha1(fullPath);

            // SHA1-first lookup.
            if (dbBySha1.TryGetValue(actualSha1, out var da))
            {
                matchedDaIds.Add(da.DerivedArtifactId);

                if (da.IsUnwanted)
                {
                    entries.Add(new LocalArchiveEntry
                    {
                        Classification     = LocalArchiveClass.UnwantedArchiveArtifact,
                        FileName           = fileName,
                        RelativePath       = relPath,
                        DerivedArtifactId  = da.DerivedArtifactId,
                        ContentIdentityKey = da.ContentIdentityKey,
                        ExpectedSha1       = da.ExpectedSha1,
                        ActualSha1         = actualSha1,
                        IsRepairable       = true,
                        Note               = "Linked to an unwanted release. Move to incoming-skip.",
                    });
                    progress?.Report(new("archive-unwanted-found", fileName, "Unwanted release"));
                }
                else
                {
                    // Check whether this artifact is already on an assigned volume.
                    if (assignedVolumes is not null &&
                        assignedVolumes.TryGetValue(da.DerivedArtifactId, out var vi))
                    {
                        if (vi.VolumeRootPath is null)
                        {
                            // Volume assigned but not reachable anywhere.
                            entries.Add(new LocalArchiveEntry
                            {
                                Classification      = LocalArchiveClass.AssignedVolumeUnavailable,
                                FileName            = fileName,
                                RelativePath        = relPath,
                                DerivedArtifactId   = da.DerivedArtifactId,
                                ContentIdentityKey  = da.ContentIdentityKey,
                                ExpectedSha1        = da.ExpectedSha1,
                                ActualSha1          = actualSha1,
                                IsRepairable        = false,
                                Note                = $"Assigned to volume '{vi.VolumeLabel}' which is not currently reachable.",
                                AssignedVolumeLabel = vi.VolumeLabel,
                            });
                            progress?.Report(new("archive-volume-unavailable", fileName, vi.VolumeLabel));
                        }
                        else
                        {
                            // Volume is reachable — this archive copy is redundant.
                            var volumeFilePath = Path.Combine(vi.VolumeRootPath, fileName);
                            entries.Add(new LocalArchiveEntry
                            {
                                Classification      = LocalArchiveClass.RedundantArchiveCopy,
                                FileName            = fileName,
                                RelativePath        = relPath,
                                DerivedArtifactId   = da.DerivedArtifactId,
                                ContentIdentityKey  = da.ContentIdentityKey,
                                ExpectedSha1        = da.ExpectedSha1,
                                ActualSha1          = actualSha1,
                                IsRepairable        = true,
                                Note                = $"Already on volume '{vi.VolumeLabel}'. Archive copy can be moved to incoming-skip.",
                                AssignedVolumeLabel = vi.VolumeLabel,
                                VolumeFilePath      = volumeFilePath,
                            });
                            progress?.Report(new("archive-redundant-copy", fileName, vi.VolumeLabel));
                        }
                    }
                    else
                    {
                        entries.Add(new LocalArchiveEntry
                        {
                            Classification     = LocalArchiveClass.WantedArchiveOk,
                            FileName           = fileName,
                            RelativePath       = relPath,
                            DerivedArtifactId  = da.DerivedArtifactId,
                            ContentIdentityKey = da.ContentIdentityKey,
                            ExpectedSha1       = da.ExpectedSha1,
                            ActualSha1         = actualSha1,
                            IsRepairable       = false,
                            Note               = "OK",
                        });
                        progress?.Report(new("archive-wanted-ok", fileName, ""));
                    }
                }
                continue;
            }

            // SHA1 not in DB — fall back to filename lookup.
            if (dbByFileName.TryGetValue(fileName, out var daByName))
            {
                if (!string.IsNullOrEmpty(daByName.ExpectedSha1))
                {
                    // Filename matched but hash differs → HashMismatch.
                    entries.Add(new LocalArchiveEntry
                    {
                        Classification     = LocalArchiveClass.ArchiveHashMismatch,
                        FileName           = fileName,
                        RelativePath       = relPath,
                        DerivedArtifactId  = daByName.DerivedArtifactId,
                        ContentIdentityKey = daByName.ContentIdentityKey,
                        ExpectedSha1       = daByName.ExpectedSha1,
                        ActualSha1         = actualSha1,
                        IsRepairable       = true,
                        Note               = $"SHA-1 mismatch: expected {daByName.ExpectedSha1[..8]}… got {actualSha1[..8]}…",
                    });
                    progress?.Report(new("archive-hash-mismatch", fileName, "Hash mismatch"));
                }
                else
                {
                    // No expected SHA-1 in DB — accept by filename (backward compat).
                    matchedDaIds.Add(daByName.DerivedArtifactId);
                    if (daByName.IsUnwanted)
                    {
                        entries.Add(new LocalArchiveEntry
                        {
                            Classification     = LocalArchiveClass.UnwantedArchiveArtifact,
                            FileName           = fileName,
                            RelativePath       = relPath,
                            DerivedArtifactId  = daByName.DerivedArtifactId,
                            ContentIdentityKey = daByName.ContentIdentityKey,
                            IsRepairable       = true,
                            Note               = "Linked to an unwanted release (no expected hash on record).",
                        });
                        progress?.Report(new("archive-unwanted-found", fileName, "Unwanted (no hash)"));
                    }
                    else
                    {
                        entries.Add(new LocalArchiveEntry
                        {
                            Classification     = LocalArchiveClass.WantedArchiveOk,
                            FileName           = fileName,
                            RelativePath       = relPath,
                            DerivedArtifactId  = daByName.DerivedArtifactId,
                            ContentIdentityKey = daByName.ContentIdentityKey,
                            IsRepairable       = false,
                            Note               = "OK (no expected hash on record)",
                        });
                        progress?.Report(new("archive-wanted-ok", fileName, "No expected hash"));
                    }
                }
                continue;
            }

            // No DB match at all — truly unknown.
            entries.Add(new LocalArchiveEntry
            {
                Classification = LocalArchiveClass.UnknownArchiveFile,
                FileName       = fileName,
                RelativePath   = relPath,
                ActualSha1     = actualSha1,
                IsRepairable   = true,
                Note           = "No DB artifact matches this file's hash or filename.",
            });
            progress?.Report(new("archive-unknown-found", fileName, "Unknown file"));
        }

        // Compute absent-from-archive diagnostic (optional, not in main entries).
        var absentCount = dbArtifacts.Count(a => !matchedDaIds.Contains(a.DerivedArtifactId));

        return new LocalArchiveVerifyPlan
        {
            PlatformId            = platformId,
            DatLineId             = datLineId,
            ArchiveDir            = archiveDir,
            Entries               = entries,
            AbsentFromArchiveCount = absentCount,
        };
    }

    // ── Repair ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Repairs all repairable entries in the plan:
    /// - RedundantArchiveCopy: re-verify volume copy, then move archive to incoming-skip. No DB changes.
    /// - UnwantedArchiveArtifact: move to incoming-skip, remove DA+RCL rows.
    /// - UnknownArchiveFile: move to incoming-skip, no DB changes.
    /// - ArchiveHashMismatch: move to incoming-skip, no DB changes.
    /// Release status is never touched — unwanted releases remain unwanted.
    /// </summary>
    public LocalArchiveRepairResult Repair(
        LocalArchiveVerifyPlan                 plan,
        DatLineStore                           store,
        IProgress<LocalArchiveVerifyProgress>? progress = null)
    {
        var log        = new List<string>();
        int movedCount = 0, removedCount = 0;

        var archiveDir = plan.ArchiveDir;
        var skipDir    = Path.Combine(_appRoot, "incoming-skip", plan.PlatformId);
        Directory.CreateDirectory(skipDir);

        foreach (var entry in plan.Entries.Where(e => e.IsRepairable))
        {
            var fullPath = Path.Combine(archiveDir, entry.RelativePath);
            if (!File.Exists(fullPath))
            {
                log.Add($"already-absent  {entry.FileName}");
                progress?.Report(new("archive-repair-skipped", entry.FileName, "Already absent"));
                continue;
            }

            // RedundantArchiveCopy: re-verify volume copy BEFORE moving the archive.
            if (entry.Classification == LocalArchiveClass.RedundantArchiveCopy)
            {
                if (entry.VolumeFilePath is null || !File.Exists(entry.VolumeFilePath))
                {
                    log.Add($"volume-copy-missing  {entry.FileName}  — volume file gone, keeping archive");
                    progress?.Report(new("archive-volume-copy-missing", entry.FileName,
                        entry.VolumeFilePath ?? "(path unknown)"));
                    continue;
                }
                var volumeSha1 = ComputeSha1(entry.VolumeFilePath);
                if (!string.Equals(volumeSha1, entry.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    log.Add($"volume-copy-corrupt  {entry.FileName}  " +
                        $"expected={entry.ExpectedSha1[..8]}… got={volumeSha1[..8]}… — keeping archive");
                    progress?.Report(new("archive-volume-copy-missing", entry.FileName,
                        $"Volume copy corrupt ({volumeSha1[..8]}…)"));
                    continue;
                }
                // Volume copy is verified — safe to move the archive.
                progress?.Report(new("archive-repair-moving", entry.FileName,
                    $"→ incoming-skip/{plan.PlatformId}/"));
                var rdest = GetCollisionSafePath(skipDir, entry.FileName);
                try
                {
                    File.Move(fullPath, rdest, overwrite: false);
                    log.Add($"redundant-moved  {entry.FileName}  volume-ok  →  {rdest}");
                    movedCount++;
                    // No DB changes: DA, VA, and release rows are untouched.
                    progress?.Report(new("archive-redundant-moved", entry.FileName,
                        entry.AssignedVolumeLabel ?? ""));
                }
                catch (Exception ex)
                {
                    log.Add($"move-failed  {entry.FileName}  {ex.Message}");
                    progress?.Report(new("archive-error", entry.FileName, ex.Message));
                }
                continue;
            }

            progress?.Report(new("archive-repair-moving", entry.FileName,
                $"→ incoming-skip/{plan.PlatformId}/"));

            var dest = GetCollisionSafePath(skipDir, entry.FileName);
            try
            {
                File.Move(fullPath, dest, overwrite: false);
                log.Add($"moved-to-skip  {entry.FileName}  →  {dest}");
                movedCount++;
                progress?.Report(new("archive-repair-moved", entry.FileName, Path.GetFileName(dest)));
            }
            catch (Exception ex)
            {
                log.Add($"move-failed  {entry.FileName}  {ex.Message}");
                progress?.Report(new("archive-error", entry.FileName, ex.Message));
                continue;
            }

            // Remove DA+RCL rows only for unwanted artifacts (not for unknown files).
            if (entry.Classification == LocalArchiveClass.UnwantedArchiveArtifact &&
                entry.DerivedArtifactId is not null && entry.ContentIdentityKey is not null)
            {
                try
                {
                    store.DeleteDerivedArtifactAndLinks(entry.DerivedArtifactId, entry.ContentIdentityKey);
                    log.Add($"db-removed  da={entry.DerivedArtifactId}");
                    removedCount++;
                }
                catch (Exception ex)
                {
                    log.Add($"db-remove-failed  da={entry.DerivedArtifactId}  {ex.Message}");
                }
            }
        }

        return new LocalArchiveRepairResult
        {
            Success       = true,
            MovedToSkip   = movedCount,
            RemovedDbRows = removedCount,
            Log           = log,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSha1(string filePath)
    {
        using var fs   = File.OpenRead(filePath);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
    }

    internal static string GetCollisionSafePath(string dir, string fileName)
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
}
