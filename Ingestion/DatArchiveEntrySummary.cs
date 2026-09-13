using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>One representative authoritative archive entry. <see cref="LeafId"/> identifies the leaf DAT (used for Group display).</summary>
public sealed record DatArchiveEntryExample(string LeafId, string ReleaseName, string FileName);

/// <summary>
/// Pure, preventive-UX summary of the archive files a DAT itself declares as authoritative entries (e.g. a TOSEC
/// release whose expected file is <c>DISK1.ZIP</c>). It inspects ONLY imported <c>release_files</c> metadata — never
/// incoming, staging, extracted members or runtime containers — and reuses the single container-extension authority
/// <see cref="ArchiveContainerExtensions"/> (<c>.zip</c>/<c>.7z</c>/<c>.rar</c>, case-insensitive). Nothing is persisted.
///
/// <para>Counts: <see cref="EntryCount"/> = authoritative archive entries; <see cref="ReleaseCount"/> = distinct releases
/// with at least one archive entry; <see cref="LeafCount"/> = leaves with at least one (Group merge). Examples are
/// deterministic (leaf, release name, file name — Ordinal) and capped at <see cref="MaxExamples"/>; only the capped
/// examples are retained, so large DATs never keep their file lists alive in the summary.</para>
/// </summary>
public sealed class DatArchiveEntrySummary
{
    public const int MaxExamples = 3;

    public int EntryCount   { get; }
    public int ReleaseCount { get; }
    public int LeafCount    { get; }
    public IReadOnlyList<DatArchiveEntryExample> Examples { get; }
    /// <summary>Leaves that could not be scanned (missing/unreadable DB) — the summary may be incomplete.</summary>
    public IReadOnlyList<string> UnscannedLeafIds { get; }

    public bool HasArchiveEntries => EntryCount > 0;
    public bool IsIncomplete      => UnscannedLeafIds.Count > 0;
    /// <summary>Archive entries not listed as examples (drives the "+ N more" line).</summary>
    public int MoreCount          => Math.Max(0, EntryCount - Examples.Count);

    public static readonly DatArchiveEntrySummary Empty =
        new(0, 0, 0, Array.Empty<DatArchiveEntryExample>(), Array.Empty<string>());

    private DatArchiveEntrySummary(
        int entryCount, int releaseCount, int leafCount,
        IReadOnlyList<DatArchiveEntryExample> examples, IReadOnlyList<string> unscannedLeafIds)
    {
        EntryCount       = entryCount;
        ReleaseCount     = releaseCount;
        LeafCount        = leafCount;
        Examples         = examples;
        UnscannedLeafIds = unscannedLeafIds;
    }

    /// <summary>True when a DAT-declared file name/relative path has a recognized archive-container extension.</summary>
    public static bool IsArchiveEntry(string? romName)
        => !string.IsNullOrEmpty(romName) && ArchiveContainerExtensions.IsContainer(romName);

    /// <summary>
    /// Releases included in the analysis: every release except <c>outdated</c> ones — the same non-outdated filter
    /// ingestion uses. <c>unwanted</c> releases are deliberately INCLUDED: this is a property of the DAT configuration
    /// and wanted state can change later.
    /// </summary>
    public static bool IsAnalyzedRelease(ReleaseRecord release)
        => !string.Equals(release.Status, "outdated", StringComparison.OrdinalIgnoreCase);

    /// <summary>Summarizes one leaf DAT from its already-loaded releases and release files.</summary>
    public static DatArchiveEntrySummary ForLeaf(
        string                                               leafId,
        IEnumerable<ReleaseRecord>                           releases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>> filesByRelease)
    {
        int entries = 0, affectedReleases = 0;
        var examples = new List<DatArchiveEntryExample>();

        foreach (var r in releases)
        {
            if (!IsAnalyzedRelease(r)) continue;
            if (!filesByRelease.TryGetValue(r.Id, out var files)) continue;

            int inRelease = 0;
            foreach (var f in files)
            {
                if (!IsArchiveEntry(f.RomName)) continue;
                inRelease++;
                examples.Add(new DatArchiveEntryExample(leafId, r.Name, f.RomName));
            }

            if (inRelease > 0) { entries += inRelease; affectedReleases++; }
            if (examples.Count > 64) examples = TopExamples(examples);   // bounded: the top-N of a union needs only each part's top-N
        }

        return new DatArchiveEntrySummary(
            entries, affectedReleases, entries > 0 ? 1 : 0, TopExamples(examples), Array.Empty<string>());
    }

    /// <summary>Merges per-leaf summaries (Group). Leaves that could not be scanned are carried as a diagnostic.</summary>
    public static DatArchiveEntrySummary Merge(
        IEnumerable<DatArchiveEntrySummary> parts, IEnumerable<string>? unscannedLeafIds = null)
    {
        var list = parts.ToList();
        var unscanned = list.SelectMany(p => p.UnscannedLeafIds)
            .Concat(unscannedLeafIds ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new DatArchiveEntrySummary(
            list.Sum(p => p.EntryCount),
            list.Sum(p => p.ReleaseCount),     // release ids are leaf-scoped, so per-leaf counts never overlap
            list.Sum(p => p.LeafCount),
            TopExamples(list.SelectMany(p => p.Examples)),
            unscanned);
    }

    private static List<DatArchiveEntryExample> TopExamples(IEnumerable<DatArchiveEntryExample> examples)
        => examples
            .OrderBy(e => e.LeafId,      StringComparer.Ordinal)
            .ThenBy(e => e.ReleaseName,  StringComparer.Ordinal)
            .ThenBy(e => e.FileName,     StringComparer.Ordinal)
            .Take(MaxExamples)
            .ToList();
}

/// <summary>Severity of the Configure DAT/Group archive-entry notice. Never affects validation or Save/Apply.</summary>
public enum DatArchiveWarningSeverity { None, Info, Warning }

/// <summary>Display-ready archive-entry notice (pure data; rendered by the dialogs).</summary>
public sealed record DatArchiveWarning(
    DatArchiveWarningSeverity Severity,
    string                    Title,
    IReadOnlyList<string>     Message,
    IReadOnlyList<string>     Examples,
    string?                   MoreLine,
    string?                   IncompleteNote)
{
    public static readonly DatArchiveWarning None =
        new(DatArchiveWarningSeverity.None, "", Array.Empty<string>(), Array.Empty<string>(), null, null);
}

/// <summary>
/// Pure wording/severity for the archive-entry notice, contextual to the selected File Handling. Preventive only: it
/// never implies extraction is always wrong, never changes File Handling, and never blocks Save/Apply.
/// </summary>
public static class DatArchiveEntryWarning
{
    public const string Title           = "Archive files detected in DAT";
    public const string IncompleteTitle = "Archive entry scan incomplete";

    public static DatArchiveWarning Describe(DatArchiveEntrySummary summary, string? fileHandling, bool isGroup)
    {
        string? incomplete = summary.IsIncomplete
            ? $"{Count(summary.UnscannedLeafIds.Count, "leaf DAT", "leaf DATs")} could not be scanned — this summary may be incomplete."
            : null;

        if (!summary.HasArchiveEntries)
            return incomplete is null
                ? DatArchiveWarning.None
                : new DatArchiveWarning(DatArchiveWarningSeverity.Info, IncompleteTitle,
                    Array.Empty<string>(), Array.Empty<string>(), null, incomplete);

        var counts = $"{Count(summary.EntryCount, "archive file", "archive files")} in {Count(summary.ReleaseCount, "release", "releases")}";
        var message = new List<string>
        {
            isGroup
                ? $"This Group contains {counts} across {Count(summary.LeafCount, "leaf DAT", "leaf DATs")} as authoritative entries."
                : $"This DAT contains {counts} as authoritative entries.",
            "These may be expected software files rather than distribution containers.",
        };

        DatArchiveWarningSeverity severity;
        switch (fileHandling)
        {
            case "archives_pre_extraction":
                severity = DatArchiveWarningSeverity.Warning;
                message.Add("File Handling is set to Archives Pre-Extraction.");
                message.Add("Archive containers found in incoming are extracted before matching. If one of the archive " +
                            "files listed by this DAT is expected intact, it may not be available to satisfy that DAT requirement.");
                message.Add("Verify that these archive entries are intended to be treated as distribution containers " +
                            "before using pre-extraction.");
                break;

            case "all_files":
                severity = DatArchiveWarningSeverity.Info;
                message.Add("File Handling is set to All Files. Archive files in incoming will be treated as normal " +
                            "files and can match authoritative DAT entries.");
                break;

            default:   // mixed / unset / unknown — safe informational fallback
                severity = DatArchiveWarningSeverity.Info;
                message.Add("File Handling is not set to a single known mode. Review it before ingestion: Archives " +
                            "Pre-Extraction extracts archive containers found in incoming before matching.");
                break;
        }

        var examples = summary.Examples
            .Select(e => isGroup
                ? $"{e.LeafId} — {e.ReleaseName} — {e.FileName}"
                : $"{e.ReleaseName} — {e.FileName}")
            .ToList();
        var more = summary.MoreCount > 0 ? $"+ {summary.MoreCount:N0} more" : null;

        return new DatArchiveWarning(severity, Title, message, examples, more, incomplete);
    }

    private static string Count(int n, string singular, string plural) => $"{n:N0} {(n == 1 ? singular : plural)}";
}
