using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Arkadia;

public static class AmpReportHelpers
{
    public static int GetMetadataPercent(AmpExportPlan plan) =>
        plan.ReleaseCount == 0 ? 0 : plan.ReleasesWithMetadata * 100 / plan.ReleaseCount;

    public static int GetMediaPercent(AmpExportPlan plan) =>
        plan.ReleaseCount == 0 ? 0 : plan.ReleasesWithMedia * 100 / plan.ReleaseCount;

    public static int GetErrorCount(AmpExportPlan plan) =>
        plan.Releases.Sum(r => r.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Error))
        + plan.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Error);

    public static int GetWarningCount(AmpExportPlan plan) =>
        plan.Releases.Sum(r => r.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Warning))
        + plan.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Warning);

    public static int GetInfoCount(AmpExportPlan plan) =>
        plan.Releases.Sum(r => r.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Info))
        + plan.Issues.Count(i => i.Severity == AmpExportPlanSeverity.Info);

    public static int GetReleaseIssueCount(AmpExportPlanRelease release) =>
        release.Issues.Count;

    public static bool HasErrors(AmpExportPlan plan) => GetErrorCount(plan) > 0;

    public static string FormatBytes(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            0                     => "0 B",
            < 1024                => $"{bytes} B",
            < 1024 * 1024         => $"{(bytes / 1024.0).ToString("F1", c)} KB",
            < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("F1", c)} MB",
            _                     => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("F2", c)} GB",
        };
    }

    // Produces the canonical archive path for a media entry inside an AMP ZIP.
    // Format: media/{mediaType}/{releaseId}/{fileName}
    // Per-release namespacing prevents cross-release filename collisions.
    public static string BuildArchivePath(string mediaType, string releaseId, string filePath)
    {
        var safeType = SanitizeZipSegment(mediaType);
        var safeId   = SanitizeZipSegment(releaseId);
        var fileName = SanitizeZipSegment(Path.GetFileName(filePath));
        return $"media/{safeType}/{safeId}/{fileName}";
    }

    public static string SuggestedAmpFileName(AmpExportPlan plan)
    {
        var raw     = plan.SystemName + "-" + plan.DatLineId;
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var sb      = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(invalid.Contains(ch) || ch == ' ' ? '-' : ch);
        var s = sb.ToString();
        while (s.Contains("--"))
            s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length == 0) s = "Arkadia-Media-Pack";
        return s + ".amp";
    }

    internal static string SanitizeZipSegment(string s) =>
        s.Replace('\\', '_').Replace('/', '_').Replace('\0', '_').TrimStart('.');
}
