using System;
using System.IO;

namespace Arkadia.Ingestion;

/// <summary>
/// Helpers for the Phase 6 incoming → staging file operation.
/// Extracted here so they can be unit-tested without instantiating MainWindow.
/// </summary>
internal static class StagingHelpers
{
    /// <summary>
    /// Returns true when both paths reside on the same volume root, making a
    /// same-volume NTFS rename (File.Move) safe and atomic.
    /// Returns false on any exception so the caller always falls back to copy.
    /// </summary>
    internal static bool SameVolume(string path1, string path2)
    {
        try
        {
            var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
            var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
            return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Moves or copies <paramref name="srcPath"/> to <paramref name="destPath"/>.
    /// A move is used only when <paramref name="pendingCount"/> == 1 (no other release
    /// targets need this file) AND both paths are on the same volume.
    /// Falls back to File.Copy when the move attempt throws.
    /// </summary>
    /// <param name="opName">
    /// Set to <c>"stage-moved"</c> when a move was performed, <c>"copy"</c> otherwise.
    /// </param>
    internal static void StageFile(
        string  srcPath,
        string  destPath,
        int     pendingCount,
        out string opName)
    {
        if (pendingCount == 1 && SameVolume(srcPath, destPath))
        {
            try
            {
                File.Move(srcPath, destPath, overwrite: true);
                opName = "stage-moved";
                return;
            }
            catch { /* same-volume move failed (permissions, lock, …) — fall through to copy */ }
        }

        File.Copy(srcPath, destPath, overwrite: true);
        opName = "copy";
    }
}
