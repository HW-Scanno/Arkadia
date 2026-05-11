using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Arkadia;

public sealed record AmpPackageVerificationResult(
    string  AmpFilePath,
    string  FileName,
    bool    FileExists,
    bool    ZipReadable,
    bool    ManifestPresent,
    bool    ManifestValid,
    bool    ReleasesPresent,
    bool    ReleasesValid,
    bool    HashFilePresent,
    bool    HashFileValid,
    int     ManifestReleaseCount,
    int     ManifestMediaFileCount,
    int     ReleasesReleaseCount,
    int     ReleasesMediaFileCount,
    int     HashFileCount,
    int     MediaFilesFound,
    int     MediaFilesMissing,
    int     ZeroByteMediaFiles,
    int     Sha256Mismatches,
    int     ForbiddenContentViolations,
    int     DuplicateReleaseKeys,
    int     DuplicateArchivePaths,
    IReadOnlyList<AmpPackageVerificationIssue> Issues)
{
    public bool   HasErrors   => Issues.Any(i => i.Severity == AmpPackageVerificationSeverity.Error);
    public bool   HasWarnings => Issues.Any(i => i.Severity == AmpPackageVerificationSeverity.Warning);
    public string Status      => HasErrors ? "Error" : HasWarnings ? "Warning" : "Valid";

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Package:  {FileName}");
        sb.AppendLine($"Path:     {AmpFilePath}");
        sb.AppendLine($"Status:   {Status}");
        sb.AppendLine();
        sb.AppendLine("Summary");
        sb.AppendLine($"  Releases (manifest):     {ManifestReleaseCount}");
        sb.AppendLine($"  Releases (releases.json):{ReleasesReleaseCount,4}");
        sb.AppendLine($"  Media files (manifest):  {ManifestMediaFileCount}");
        sb.AppendLine($"  Media files (releases):  {ReleasesMediaFileCount}");
        sb.AppendLine($"  Hash entries:            {HashFileCount}");
        sb.AppendLine($"  Media files found:       {MediaFilesFound}");
        sb.AppendLine($"  Media files missing:     {MediaFilesMissing}");
        sb.AppendLine($"  Zero-byte media:         {ZeroByteMediaFiles}");
        sb.AppendLine($"  SHA-256 mismatches:      {Sha256Mismatches}");
        sb.AppendLine($"  Duplicate release keys:  {DuplicateReleaseKeys}");
        sb.AppendLine($"  Duplicate archive paths: {DuplicateArchivePaths}");
        sb.AppendLine($"  Forbidden content:       {ForbiddenContentViolations}");

        static string Label(AmpPackageVerificationSeverity s) => s switch
        {
            AmpPackageVerificationSeverity.Error   => "Errors",
            AmpPackageVerificationSeverity.Warning => "Warnings",
            _                                      => "Info",
        };

        foreach (var sev in new[]
        {
            AmpPackageVerificationSeverity.Error,
            AmpPackageVerificationSeverity.Warning,
            AmpPackageVerificationSeverity.Info,
        })
        {
            var group = Issues.Where(i => i.Severity == sev).ToList();
            if (group.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine(Label(sev) + ":");
            foreach (var issue in group)
                sb.AppendLine($"  [{issue.Area}] {issue.Message}");
        }

        return sb.ToString();
    }
}
