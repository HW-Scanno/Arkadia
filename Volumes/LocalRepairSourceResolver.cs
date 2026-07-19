using System.IO;
using Arkadia.Ingestion;

namespace Arkadia.Volumes;

/// <summary>
/// Resolves a local recovery source for a missing/corrupt volume artifact during
/// Volume Repair. Shared production authority so both the pipeline and its tests
/// resolve paths identically.
///
/// The archive copy is resolved from the DB <c>relative_path</c> — the authoritative
/// location — so it works regardless of the physical archive layout (flat CHD such as
/// <c>archive/dc/dc-redump-gd/Sonic Adventure (USA).chd</c>, or legacy release-foldered
/// <c>archive/ps2/dl/Release/Game.rom</c>). It never reconstructs the archive path from
/// platform/DAT-line/release-name.
///
/// The <c>source</c> fallback is the temporary transform-input area, which ingestion
/// writes release-foldered; it is best-effort and normally empty after a successful run.
/// </summary>
public static class LocalRepairSourceResolver
{
    /// <summary>
    /// Returns the first existing local recovery source (archive preferred, then source),
    /// or null when neither exists (the caller then falls back to incoming-repair).
    /// This only resolves a path; the caller still copies and hash-verifies before use.
    /// </summary>
    public static string? Resolve(
        string appRoot,
        string relativePath,
        string platformId,
        string datLineId,
        string releaseName,
        string fileName)
    {
        // Archive: authoritative DB relative_path (layout-agnostic).
        if (!string.IsNullOrEmpty(relativePath))
        {
            var archivePath = Path.Combine(appRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(archivePath)) return archivePath;
        }

        // Source fallback: temporary transform-input area (release-foldered).
        var safe       = IngestionPaths.SafeFolderName(releaseName);
        var sourcePath = Path.Combine(appRoot, "source", platformId, datLineId, safe, fileName);
        if (File.Exists(sourcePath)) return sourcePath;

        return null;
    }
}
