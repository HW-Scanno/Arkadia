using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Arkadia;

public sealed class ArkRestoreService
{
    public ArkRestoreResult Restore(
        string            arkFilePath,
        string            targetDataDir,
        bool              overwrite = false,
        CancellationToken ct        = default)
    {
        if (string.IsNullOrWhiteSpace(arkFilePath))
            throw new ArgumentException("ARK file path must not be empty.", nameof(arkFilePath));
        if (string.IsNullOrWhiteSpace(targetDataDir))
            throw new ArgumentException("Target data directory must not be empty.", nameof(targetDataDir));

        // ── 1. Plan ───────────────────────────────────────────────────────────

        var plan = new ArkRestorePlanService().PlanRestore(arkFilePath, targetDataDir);

        if (!plan.PackageValid || plan.Issues.Count > 0)
        {
            var reason = plan.Issues.Count > 0 ? plan.Issues[0] : "Package verification failed.";
            throw new InvalidOperationException(reason);
        }

        // ── 2. Overwrite policy ───────────────────────────────────────────────

        if (plan.RequiresOverwrite && !overwrite)
            throw new InvalidOperationException(
                "Target data directory is not empty. ARK restore is full replacement only. " +
                "Pass overwrite=true to replace it.");

        // ── 3. Create staging dir ─────────────────────────────────────────────

        var fullTargetPath = Path.GetFullPath(targetDataDir);
        var parent         = Directory.GetParent(fullTargetPath)
            ?? throw new InvalidOperationException(
                "Cannot determine parent directory of the target data directory.");

        var stamp      = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stagingDir = Path.Combine(parent.FullName, ".ark-restore-" + stamp);
        Directory.CreateDirectory(stagingDir);

        var warnings      = new List<string>();
        var issues        = new List<string>();
        int  restoredCount = 0;
        long restoredBytes = 0;
        string? previousBackupDir = null;
        bool stagingCommitted     = false;

        try
        {
            // ── 4. Extract planned entries ────────────────────────────────────

            var fullTargetBase  = fullTargetPath.TrimEnd(Path.DirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;
            var fullStagingBase = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;

            using (var zip = ZipFile.OpenRead(arkFilePath))
            {
                var entryLookup = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);

                foreach (var planEntry in plan.Entries.Where(e => e.WillRestore))
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(planEntry.TargetPath))
                    {
                        issues.Add($"No target path for planned entry: '{planEntry.ArchivePath}'.");
                        continue;
                    }

                    if (!entryLookup.TryGetValue(planEntry.ArchivePath, out var zipEntry))
                    {
                        issues.Add($"Planned entry not found in ZIP: '{planEntry.ArchivePath}'.");
                        continue;
                    }

                    var fullEntryTarget = Path.GetFullPath(planEntry.TargetPath);
                    if (!fullEntryTarget.StartsWith(fullTargetBase, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Entry maps outside target data directory: '{planEntry.ArchivePath}'.");

                    var relative      = fullEntryTarget[fullTargetBase.Length..];
                    var stagingTarget = Path.GetFullPath(Path.Combine(stagingDir, relative));

                    if (!stagingTarget.StartsWith(fullStagingBase, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Staging path is outside staging directory: '{planEntry.ArchivePath}'.");

                    var stagingParent = Path.GetDirectoryName(stagingTarget)!;
                    if (!string.IsNullOrEmpty(stagingParent))
                        Directory.CreateDirectory(stagingParent);

                    using var src  = zipEntry.Open();
                    using var dest = new FileStream(
                        stagingTarget, FileMode.Create, FileAccess.Write, FileShare.None);
                    src.CopyTo(dest);

                    restoredCount++;
                    restoredBytes += zipEntry.Length;
                }
            }

            // ── 5. Verify staging completeness ────────────────────────────────

            if (!File.Exists(Path.Combine(stagingDir, "catalog.db")))
                throw new InvalidOperationException(
                    "Staging directory is missing catalog.db after extraction.");

            // ── 6. Commit ─────────────────────────────────────────────────────

            if (!Directory.Exists(fullTargetPath))
            {
                Directory.Move(stagingDir, fullTargetPath);
            }
            else if (!Directory.EnumerateFileSystemEntries(fullTargetPath).Any())
            {
                Directory.Delete(fullTargetPath, recursive: false);
                Directory.Move(stagingDir, fullTargetPath);
            }
            else
            {
                // Non-empty target + overwrite=true
                previousBackupDir = fullTargetPath + ".pre-ark-restore-" + stamp;
                Directory.Move(fullTargetPath, previousBackupDir);
                try
                {
                    Directory.Move(stagingDir, fullTargetPath);
                }
                catch (Exception ex)
                {
                    // Recovery: try to move staging to target so data is not lost
                    try
                    {
                        if (!Directory.Exists(fullTargetPath) && Directory.Exists(stagingDir))
                            Directory.Move(stagingDir, fullTargetPath);
                    }
                    catch { }
                    throw new InvalidOperationException(
                        $"Commit failed after moving previous data to '{previousBackupDir}'. " +
                        $"Staging is at '{stagingDir}'. Error: {ex.Message}");
                }
            }

            stagingCommitted = true;
        }
        catch
        {
            if (!stagingCommitted && Directory.Exists(stagingDir))
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            throw;
        }

        // ── 7. Post-restore warnings ──────────────────────────────────────────

        warnings.Add(
            "Restore complete. Run Verify ALL / Verify Volume before trusting restored archive state.");
        warnings.Add(
            "Restored databases may contain absolute paths that require review or relocation.");

        return new ArkRestoreResult(
            Success:               true,
            ArkFilePath:           arkFilePath,
            TargetDataDir:         targetDataDir,
            StagingDir:            stagingDir,
            PreviousDataBackupDir: previousBackupDir,
            RestoredEntryCount:    restoredCount,
            RestoredBytes:         restoredBytes,
            OverwriteUsed:         overwrite && plan.RequiresOverwrite,
            Warnings:              warnings,
            Issues:                issues);
    }
}
