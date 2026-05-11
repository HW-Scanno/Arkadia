using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;

namespace Arkadia;

public sealed class AmpLocalRegistryService
{
    public string RegistryFolder { get; }

    public AmpLocalRegistryService(string dataDir)
    {
        RegistryFolder = Path.Combine(
            dataDir,
            ArkadiaFolders.ScrapeCache,
            ArkadiaFolders.ArkadiaMediaPacks);
    }

    public void EnsureFolder() =>
        Directory.CreateDirectory(RegistryFolder);

    public IReadOnlyList<AmpLocalPackageInfo> ListPackages()
    {
        EnsureFolder();

        return Directory
            .GetFiles(RegistryFolder, "*.amp", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(BuildPackageInfo)
            .ToList();
    }

    public IReadOnlyList<AmpLocalPackageInfo> ListPackagesForScope(
        string hardwareFamilyId,
        string datLineId)
    {
        return ListPackages()
            .Where(p =>
                p.Status != "Unreadable" &&
                string.Equals(p.HardwareFamilyId, hardwareFamilyId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.DatLineId, datLineId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public AmpLocalPackageInfo VerifyPackage(string ampFilePath)
    {
        var fi     = new FileInfo(ampFilePath);
        var sha256 = ReleaseMediaCurationService.ComputeSha256(ampFilePath) ?? "";

        var result = new AmpPackageVerifierService().Verify(ampFilePath);

        AmpManifestReader.TryReadManifest(ampFilePath, out var manifest);

        return new AmpLocalPackageInfo(
            FilePath:           ampFilePath,
            FileName:           Path.GetFileName(ampFilePath),
            PackageBytes:       fi.Exists ? fi.Length : 0L,
            PackageSha256:      sha256,
            Status:             result.Status,
            HasErrors:          result.HasErrors,
            HasWarnings:        result.HasWarnings,
            FormatName:         manifest.FormatName,
            FormatVersion:      manifest.FormatVersion,
            HardwareFamilyId:   manifest.HardwareFamilyId,
            DatLineId:          manifest.DatLineId,
            SystemName:         manifest.SystemName,
            ReleaseCount:       manifest.ReleaseCount,
            MediaFileCount:     manifest.MediaFileCount,
            TotalMediaBytes:    manifest.TotalMediaBytes,
            ExclusionCount:     manifest.ExclusionCount,
            ExtraNotesCount:    manifest.ExtraNotesCount,
            LastWriteTimeUtc:   fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : default,
            VerificationResult: result);
    }

    public AmpLocalPackageInfo RegisterLocalPackage(
        string            sourceAmpPath,
        bool              overwrite = false,
        CancellationToken ct        = default)
    {
        if (string.IsNullOrWhiteSpace(sourceAmpPath))
            throw new ArgumentException("Source path must not be empty.", nameof(sourceAmpPath));

        if (!sourceAmpPath.EndsWith(".amp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source file must have the .amp extension.", nameof(sourceAmpPath));

        if (!File.Exists(sourceAmpPath))
            throw new FileNotFoundException("Source AMP file not found.", sourceAmpPath);

        EnsureFolder();

        var srcFull = Path.GetFullPath(sourceAmpPath);
        var regFull = Path.GetFullPath(RegistryFolder)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        if (srcFull.StartsWith(regFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source file is already inside the registry folder.");

        var fileName = Path.GetFileName(srcFull);
        var dstPath  = Path.Combine(RegistryFolder, fileName);

        if (!overwrite && File.Exists(dstPath))
            throw new InvalidOperationException(
                $"A package named '{fileName}' is already registered. Use overwrite to replace it.");

        var preResult = new AmpPackageVerifierService().Verify(srcFull);
        if (preResult.HasErrors)
            throw new InvalidOperationException(
                $"Source package failed verification and cannot be registered. Status: {preResult.Status}");

        var tmpPath = dstPath + ".tmp";
        try
        {
            File.Copy(srcFull, tmpPath, overwrite: true);
            ct.ThrowIfCancellationRequested();
            File.Move(tmpPath, dstPath, overwrite: overwrite);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }

        return VerifyPackage(dstPath);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static AmpLocalPackageInfo BuildPackageInfo(string path)
    {
        var fi     = new FileInfo(path);
        var sha256 = ReleaseMediaCurationService.ComputeSha256(path) ?? "";
        var mtime  = fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : default;

        if (AmpManifestReader.TryReadManifest(path, out var manifest))
        {
            return new AmpLocalPackageInfo(
                FilePath:           path,
                FileName:           Path.GetFileName(path),
                PackageBytes:       fi.Exists ? fi.Length : 0L,
                PackageSha256:      sha256,
                Status:             "Unverified",
                HasErrors:          false,
                HasWarnings:        false,
                FormatName:         manifest.FormatName,
                FormatVersion:      manifest.FormatVersion,
                HardwareFamilyId:   manifest.HardwareFamilyId,
                DatLineId:          manifest.DatLineId,
                SystemName:         manifest.SystemName,
                ReleaseCount:       manifest.ReleaseCount,
                MediaFileCount:     manifest.MediaFileCount,
                TotalMediaBytes:    manifest.TotalMediaBytes,
                ExclusionCount:     manifest.ExclusionCount,
                ExtraNotesCount:    manifest.ExtraNotesCount,
                LastWriteTimeUtc:   mtime,
                VerificationResult: null);
        }

        return new AmpLocalPackageInfo(
            FilePath:           path,
            FileName:           Path.GetFileName(path),
            PackageBytes:       fi.Exists ? fi.Length : 0L,
            PackageSha256:      sha256,
            Status:             "Unreadable",
            HasErrors:          true,
            HasWarnings:        false,
            FormatName:         "",
            FormatVersion:      "",
            HardwareFamilyId:   "",
            DatLineId:          "",
            SystemName:         "",
            ReleaseCount:       0,
            MediaFileCount:     0,
            TotalMediaBytes:    0L,
            ExclusionCount:     0,
            ExtraNotesCount:    0,
            LastWriteTimeUtc:   mtime,
            VerificationResult: null);
    }
}
