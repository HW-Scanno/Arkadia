using System;
using System.Text;

namespace Arkadia.Ingestion;

/// <summary>
/// Builds the textual ingestion log. Extracted from MainWindow so the counter
/// block, unwanted reporting, and operation listing can be unit-tested without
/// the UI or the ingestion pipeline. Presentation only — no behavior.
/// </summary>
public static class IngestionLogFormatter
{
    /// <summary>
    /// Renders the full ingestion log body for <paramref name="result"/>.
    /// The COUNTS block is generated from <see cref="IngestionSummary.CoreCounters"/>,
    /// so the log and the dialog summary always show the same counter set —
    /// including "Unwanted skipped", which was previously omitted.
    /// </summary>
    public static string Build(string datLineId, IngestionResult result, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ARKADIA INGESTION LOG");
        sb.AppendLine($"Date:         {utcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"DAT Line ID:  {datLineId}");
        sb.AppendLine();

        sb.AppendLine("── COUNTS ─────────────────────────────────────────────────────────────────");
        foreach (var (label, value) in IngestionSummary.CoreCounters(result))
            sb.AppendLine($"  {(label + ":").PadRight(28)}{value}");
        sb.AppendLine();

        var note = IngestionSummary.AllUnwantedNote(result);
        if (note is not null)
        {
            sb.AppendLine(note);
            sb.AppendLine();
        }

        if (result.TransformsFailed > 0 || result.ReleasesIncomplete > 0)
        {
            sb.AppendLine("── FAILURES ───────────────────────────────────────────────────────────────");
            foreach (var op in result.Operations)
            {
                if (op.Action == "transform-failed"       ||
                    op.Action == "transform-config-error" ||
                    op.Action == "incomplete-skipped"     ||
                    op.Action == "archive-collision"      ||
                    op.Action == "archive-validation-blocked")
                    sb.AppendLine($"  {op.Object,-50} | {op.Action,-22} | {op.Destination}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("── OPERATIONS ─────────────────────────────────────────────────────────────");
        foreach (var op in result.Operations)
            sb.AppendLine($"  {op.Object,-50} | {op.Action,-22} | {op.Destination}");
        sb.AppendLine();
        sb.AppendLine($"RESULT: {result.StatusText}");
        if (result.Error is not null)
            sb.AppendLine($"  ERROR: {result.Error}");

        return sb.ToString();
    }
}
