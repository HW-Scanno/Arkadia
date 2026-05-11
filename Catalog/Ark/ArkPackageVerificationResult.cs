using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Arkadia;

public sealed record ArkPackageVerificationResult(
    string ArkFilePath,
    string FileName,
    bool   FileExists,
    bool   ZipReadable,
    bool   ManifestPresent,
    bool   ManifestValid,
    bool   HashFilePresent,
    bool   HashFileValid,
    bool   CatalogDbPresent,
    int    DatLineDbCount,
    int    HashFileCount,
    int    Sha256Mismatches,
    int    UntrackedEntries,
    bool   SidecarPresent,
    bool   SidecarValid,
    IReadOnlyList<ArkPackageVerificationIssue> Issues)
{
    public bool   HasErrors   => Issues.Any(i => i.Severity == ArkPackageVerificationSeverity.Error);
    public bool   HasWarnings => Issues.Any(i => i.Severity == ArkPackageVerificationSeverity.Warning);
    public string Status      => HasErrors ? "Error" : HasWarnings ? "Warning" : "Valid";

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Package:  {FileName}");
        sb.AppendLine($"Path:     {ArkFilePath}");
        sb.AppendLine($"Status:   {Status}");
        sb.AppendLine();
        sb.AppendLine("Summary");
        sb.AppendLine($"  DAT-line databases:  {DatLineDbCount}");
        sb.AppendLine($"  Hash entries:        {HashFileCount}");
        sb.AppendLine($"  SHA-256 mismatches:  {Sha256Mismatches}");
        sb.AppendLine($"  Untracked entries:   {UntrackedEntries}");
        sb.AppendLine($"  Sidecar present:     {SidecarPresent}");
        sb.AppendLine($"  Sidecar valid:       {SidecarValid}");

        static string Label(ArkPackageVerificationSeverity s) => s switch
        {
            ArkPackageVerificationSeverity.Error   => "Errors",
            ArkPackageVerificationSeverity.Warning => "Warnings",
            _                                      => "Info",
        };

        foreach (var sev in new[]
        {
            ArkPackageVerificationSeverity.Error,
            ArkPackageVerificationSeverity.Warning,
            ArkPackageVerificationSeverity.Info,
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
