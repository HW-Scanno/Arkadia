using System;
using System.Linq;
using System.Text;

namespace Arkadia.Maintenance;

/// <summary>Plain-text rendering of a cleanup scan / result for the Clean Empty Folders dialog (pure, unit-tested).</summary>
public static class EmptyFolderReport
{
    /// <summary>Paths listed per section before the text says "… and N more" (the counts are always complete).</summary>
    public const int MaxListed = 400;

    public static string Headline(EmptyFolderScan scan)
    {
        var n = scan.CandidateCount;
        return n == 0
            ? "No empty folders found."
            : $"{n} empty folder{(n == 1 ? "" : "s")} can be removed. No files will be deleted.";
    }

    public static string Preview(EmptyFolderScan scan)
    {
        var sb = new StringBuilder();
        foreach (var r in scan.Roots)
        {
            sb.Append(r.Root.Label.ToUpperInvariant()).Append("  —  ").AppendLine(r.Root.Path);
            if (r.RootProblem is not null) sb.Append("  Not scanned: ").AppendLine(r.RootProblem);
            else if (!r.RootExists)        sb.AppendLine("  Folder not present; nothing to do.");
            else
            {
                sb.AppendLine($"  {r.Candidates.Count} empty folder{(r.Candidates.Count == 1 ? "" : "s")}");
                foreach (var c in r.Candidates.Take(MaxListed)) sb.Append("    ").AppendLine(Relative(r.Root.Path, c.Path));
                if (r.Candidates.Count > MaxListed) sb.AppendLine($"    … and {r.Candidates.Count - MaxListed} more");
                if (r.Notes.Count > 0)
                {
                    sb.AppendLine($"  Left untouched ({r.Notes.Count}):");
                    foreach (var n in r.Notes.Take(MaxListed)) sb.Append("    ").Append(Relative(r.Root.Path, n.Path)).Append("  —  ").AppendLine(n.Reason);
                    if (r.Notes.Count > MaxListed) sb.AppendLine($"    … and {r.Notes.Count - MaxListed} more");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string Summary(EmptyFolderCleanupResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Cancelled ? "Cancelled before completion." : "Cleanup finished.");
        sb.AppendLine();
        sb.AppendLine($"Removed:  {result.Removed.Count}");
        sb.AppendLine($"Skipped:  {result.Skipped.Count}");
        sb.AppendLine($"Failed:   {result.Failed.Count}");
        if (result.Cancelled) sb.AppendLine($"Not processed: {result.NotProcessed}");
        if (result.Skipped.Count > 0)
        {
            sb.AppendLine().AppendLine("Skipped:");
            foreach (var n in result.Skipped.Take(MaxListed)) sb.Append("  ").Append(n.Path).Append("  —  ").AppendLine(n.Reason);
            if (result.Skipped.Count > MaxListed) sb.AppendLine($"  … and {result.Skipped.Count - MaxListed} more");
        }
        if (result.Failed.Count > 0)
        {
            sb.AppendLine().AppendLine("Failed:");
            foreach (var n in result.Failed.Take(MaxListed)) sb.Append("  ").Append(n.Path).Append("  —  ").AppendLine(n.Reason);
            if (result.Failed.Count > MaxListed) sb.AppendLine($"  … and {result.Failed.Count - MaxListed} more");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Relative(string root, string path)
    {
        var r = root.TrimEnd('\\', '/');
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path.Length > r.Length
            ? path.Substring(r.Length).TrimStart('\\', '/')
            : path;
    }
}
