using System;
using System.IO;
using System.IO.Compression;

namespace Arkadia.Ingestion;

/// <summary>
/// The single ingestion container-extraction seam (shared by Single ingestion, Group ingestion, and Repair intake
/// via <c>RunPreIngest</c>). Policy A: the bundled 7-Zip is the PREFERRED extractor for every supported container
/// (<c>.zip</c>/<c>.7z</c>/<c>.rar</c>) whenever it is available — this is what lets ingestion read RomVault ZIPs
/// that use Zstandard, which <see cref="System.IO.Compression"/> cannot decode. The managed
/// <see cref="ZipArchive"/> loop is retained ONLY as the fallback for <c>.zip</c> when the bundled 7-Zip is absent;
/// its historical behavior (directory entries skipped, explicit destination-root containment guard, parent
/// creation, <see cref="FileMode.Create"/> overwrite, <c>entry.Open()+CopyTo</c>) is preserved verbatim.
///
/// Not an archive framework: no interfaces/strategies/registries — one focused policy method reusing the existing
/// <see cref="ProviderHelpers.ExtractWith7zip"/>. The ZIP metadata size-probe and free-space check stay in the
/// caller (they read central-directory metadata and work regardless of the entry compression method).
/// </summary>
internal static class ArchiveExtractor
{
    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationRoot"/>. When
    /// <paramref name="sevenZipPath"/> is non-null the bundled 7-Zip is used for ANY container (preferred);
    /// otherwise a <c>.zip</c> falls back to the managed extractor. A non-ZIP container with no 7-Zip is a caller
    /// pre-flight concern (it is skipped before reaching here); if it does reach here it throws.
    /// Exceptions propagate to the caller, which classifies <c>extract-failed</c> and leaves the incoming intact.
    /// </summary>
    internal static void Extract(string archivePath, string destinationRoot, string? sevenZipPath)
    {
        Directory.CreateDirectory(destinationRoot);

        if (sevenZipPath is not null)
        {
            // Preferred: bundled 7-Zip (x <archive> -o<dest> -y). Contains ../ and absolute-like entry paths
            // within the output root; handles Zstandard/Deflate64/BZip2/LZMA/etc. that managed ZIP cannot.
            ProviderHelpers.ExtractWith7zip(sevenZipPath, archivePath, destinationRoot);
            return;
        }

        var ext = Path.GetExtension(archivePath);
        if (!string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"7zip is required to extract '{ext}' archives — place tools\\7zip\\7zip.exe.");

        ExtractManagedZip(archivePath, destinationRoot);
    }

    /// <summary>
    /// Historical managed ZIP extractor (fallback only). Byte-for-byte the loop that previously lived inline in
    /// <c>RunPreIngest</c>: directory entries skipped, explicit destination-root containment guard, per-entry
    /// parent creation, overwrite, streamed copy. Supports Store/Deflate/Deflate64 only.
    /// </summary>
    private static void ExtractManagedZip(string archivePath, string destinationRoot)
    {
        var fullDestRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;

        // Explicit using-block so za is fully disposed before any later archive delete.
        using (var za = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in za.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;  // directory entry
                var relPath  = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, relPath));
                // path traversal guard
                if (!fullPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using var outStream   = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
                using var entryStream = entry.Open();
                entryStream.CopyTo(outStream);
            }
        }
    }
}
