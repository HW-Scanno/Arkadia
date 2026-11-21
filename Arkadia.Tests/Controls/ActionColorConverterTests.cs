using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Arkadia.Library;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests.Controls;

/// <summary>
/// INGESTION LOG COLOR FIX — the Product-Owner-approved colour of every Ingestion Log action
/// (<see cref="ActionColorConverter"/>): one colour per meaning, only colours the application already used, the
/// historical fallback for anything not listed, and the other dialogs that share the converter left as they were.
/// </summary>
public sealed class ActionColorConverterTests
{
    private const string Purple    = "#B39DDB";   // hashing and analysis
    private const string Blue      = "#64B5F6";   // staging and preparatory operations
    private const string Green     = "#81C784";   // ready or already satisfied
    private const string Cyan      = "#26C6DA";   // successfully acquired artifacts
    private const string Grey      = "#9E9E9E";   // unwanted exclusions and policy decisions
    private const string MutedGrey = "#888899";   // cleanup and redundant files
    private const string Amber     = "#FFB74D";   // warnings and skipped operations (also the fallback)
    private const string Red       = "#E57373";   // failures and blocked operations

    /// <summary>The approved mapping, verbatim: colour → the actions that must have it.</summary>
    private static readonly (string Hex, string[] Actions)[] Approved =
    [
        (Purple,    ["hash", "deep-hash", "deep-hash-physical-match", "deep-hash-not-applicable", "dat-hash-enriched", "rvz-verified"]),
        (Blue,      ["copy", "stage-moved", "empty-synthesized", "extract-ok", "staging-resumed"]),
        (Green,     ["release-input-assembled", "already-present"]),
        (Cyan,      ["transform", "derived-committed", "rvz-adopted-moved", "rvz-adopted-copied"]),
        (Grey,      ["unwanted-classified", "unwanted-moved", "stale-staging-unwanted-moved", "stale-source-unwanted-moved", "discarded-by-strategy"]),
        (MutedGrey, ["delete", "duplicate-deleted", "rvz-already-present", "archive-deleted", "stale-cue-only-staging-moved"]),
        (Amber,     ["skip", "rvz-no-match", "rvz-ambiguous", "rvz-logical-conflict", "rvz-unsupported-release", "dat-hash-conflict",
                     "extract-skipped", "deep-hash-unavailable", "present-without-artifact", "rebuild-required",
                     "stale-artifact-overwritten", "archive-preserved"]),
        (Red,       ["transform-config-error", "leaf-finalization-error", "cleanup-error", "incomplete-skipped", "archive-collision",
                     "archive-validation-blocked", "requirement-unsatisfiable", "rvz-existing-artifact-conflict", "rvz-target-exists"]),
    ];

    /// <summary>Every ingestion action whose name ends in <c>-failed</c> (red through the generic rule).</summary>
    private static readonly string[] FailedActions =
    [
        "copy-failed", "delete-failed", "duplicate-delete-failed", "unwanted-move-failed", "skip-failed",
        "archive-delete-failed", "extract-failed", "transform-failed", "file-commit-failed", "verify-failed",
        "release-commit-failed", "empty-synthesis-failed", "release-input-assembly-failed", "deep-hash-failed",
        "rvz-transfer-failed", "rvz-commit-failed", "stale-staging-cleanup-failed", "stale-source-cleanup-failed",
        "stale-cue-only-cleanup-failed",
    ];

    private static Color ColorOf(object? action)
    {
        var brush = ActionColorConverter.Instance.Convert(action, typeof(IBrush), null, CultureInfo.InvariantCulture);
        return Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    }

    private static void AssertColor(string expectedHex, object? action)
        => Assert.True(Color.Parse(expectedHex) == ColorOf(action), $"'{action}' must be {expectedHex}, was {ColorOf(action)}");

    public static TheoryData<string, string> ApprovedMapping()
    {
        var data = new TheoryData<string, string>();
        foreach (var (hex, actions) in Approved)
            foreach (var action in actions) data.Add(action, hex);
        return data;
    }

    // ── The approved mapping ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ApprovedMapping))]
    public void EveryApprovedAction_HasItsApprovedColour(string action, string hex)
        => AssertColor(hex, action);

    [Fact]
    public void ApprovedMapping_IsComplete_AndEachActionHasExactlyOneColour()
    {
        var all = Approved.SelectMany(g => g.Actions).ToList();
        Assert.Equal(48, all.Count);                                             // 6 + 5 + 2 + 4 + 5 + 5 + 12 + 9
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());   // no action listed under two colours
        Assert.Equal(new[] { 6, 5, 2, 4, 5, 5, 12, 9 }, Approved.Select(g => g.Actions.Length).ToArray());
        Assert.DoesNotContain(all, a => a.EndsWith("-failed", StringComparison.Ordinal));   // those are the generic rule's
    }

    [Fact]
    public void NoNewShade_OnlyColoursTheApplicationAlreadyUsed()
    {
        // The eight approved colours were all in use before; the three special rows keep their own.
        string[] allowed = [Purple, Blue, Green, Cyan, Grey, MutedGrey, Amber, Red, "#CE93D8", "#00BCD4", "#4CAF50"];
        var produced = Approved.SelectMany(g => g.Actions).Concat(FailedActions)
            .Concat(["source", "verify", "SOURCE", "CACHE", "cache-failed", "", "anything-else"])
            .Select(a => ColorOf(a)).Distinct().ToList();
        Assert.All(produced, c => Assert.Contains(c, allowed.Select(Color.Parse)));
        Assert.Equal(allowed.Length, produced.Count);   // …and every one of them is really used
    }

    // ── Reading an outcome from the colours ──────────────────────────────────

    [Fact]
    public void UnwantedSequence_ReadsAsANeutralExclusion_NeverAsAnError()
    {
        // deep-hash → unwanted-classified → unwanted-moved
        AssertColor(Purple, "deep-hash");
        AssertColor(Grey, "unwanted-classified");
        AssertColor(Grey, "unwanted-moved");

        Assert.NotEqual(ColorOf("unwanted-moved"), ColorOf("unwanted-move-failed"));   // exclusion ≠ real error
        AssertColor(Red, "unwanted-move-failed");
        Assert.NotEqual(ColorOf("unwanted-moved"), ColorOf("skip"));                   // exclusion ≠ unmatched file
        Assert.NotEqual(ColorOf("unwanted-moved"), ColorOf("delete"));                 // exclusion ≠ routine cleanup
    }

    [Fact]
    public void AdoptedRvzSequence_EndsInTheAcquiredArtifactColour()
    {
        // deep-hash → rvz-adopted-moved
        AssertColor(Purple, "deep-hash");
        AssertColor(Cyan, "rvz-adopted-moved");
        Assert.Equal(ColorOf("transform"), ColorOf("rvz-adopted-moved"));   // same cyan as every other acquired artifact
        Assert.Equal(ColorOf("hash"), ColorOf("deep-hash"));                // exactly the existing hashing purple
    }

    [Fact]
    public void UnwantedGrey_IsTheLibraryUnwantedColour()
    {
        var library = (ISolidColorBrush)StatusBrushConverter.Instance.Convert("unwanted", typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.Equal(library.Color, ColorOf("unwanted-moved"));
        Assert.Equal(library.Color, ColorOf("unwanted-classified"));
    }

    [Fact]
    public void CategoriesThatMeanDifferentThings_NeverShareAColour()
    {
        var colours = Approved.Select(g => Color.Parse(g.Hex)).ToList();
        Assert.Equal(colours.Count, colours.Distinct().Count());
    }

    // ── What must not have changed ───────────────────────────────────────────

    [Theory]
    [InlineData("hash",      "#B39DDB")]
    [InlineData("copy",      "#64B5F6")]
    [InlineData("transform", "#26C6DA")]
    [InlineData("skip",      "#FFB74D")]
    [InlineData("source",    "#81C784")]   // historical key (former name of release-input-assembled)
    [InlineData("verify",    "#CE93D8")]   // Write Volume to Disk
    [InlineData("SOURCE",    "#00BCD4")]   // Image Cache
    [InlineData("CACHE",     "#4CAF50")]   // Image Cache
    public void ColoursThatExistedBefore_AreUnchanged(string action, string hex)
        => AssertColor(hex, action);

    [Fact]
    public void Delete_IsTheOnlyPreviouslyColouredActionThatChanged()
        => AssertColor(MutedGrey, "delete");   // was #9E9E9E, now reserved for Unwanted

    // ── Generic "-failed" rule ───────────────────────────────────────────────

    public static TheoryData<string> EveryFailedAction()
    {
        var data = new TheoryData<string>();
        foreach (var a in FailedActions) data.Add(a);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFailedAction))]
    public void ActionsEndingInFailed_AreRed(string action)
        => AssertColor(Red, action);

    [Theory]
    [InlineData("cache-failed")]             // Image Cache
    [InlineData("some-future-step-failed")]  // not known to the converter: the suffix alone decides
    [InlineData("-failed")]
    public void FailedSuffix_IsGeneric(string action)
        => AssertColor(Red, action);

    [Theory]
    [InlineData("SOME-STEP-FAILED")]   // the rule is ordinal, as it always was
    [InlineData("failed")]             // no "-failed" suffix
    [InlineData("failed-step")]
    public void FailedRule_IsOrdinalAndSuffixOnly(string action)
        => AssertColor(Amber, action);

    // ── Fallback for unknown actions ─────────────────────────────────────────

    [Theory]
    [InlineData("some-new-action")]
    [InlineData("")]
    [InlineData("HASH")]          // names are case-sensitive: an unknown spelling is an unknown action
    [InlineData("Delete")]
    [InlineData("unwanted")]
    public void UnknownAction_KeepsTheHistoricalAmberFallback(string action)
        => AssertColor(Amber, action);

    [Fact]
    public void NullOrNonStringValue_KeepsTheHistoricalAmberFallback()
    {
        AssertColor(Amber, null);
        AssertColor(Amber, 42);
        AssertColor(Amber, new object());
    }

    // ── The converter's callers ──────────────────────────────────────────────

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    [Theory]
    [InlineData("Ingestion/IngestionProgressDialog.axaml")]
    [InlineData("Systems/ImageCacheProgressDialog.axaml")]
    [InlineData("Volumes/WriteVolumeToDiskDialog.axaml")]
    public void Callers_StillBindTheActionThroughThisConverter(string dialog)
    {
        var xaml = RepoFile(dialog);
        Assert.Contains("<local:ActionColorConverter x:Key=\"ActionColor\"/>", xaml);
        Assert.Contains("Foreground=\"{Binding Action, Converter={StaticResource ActionColor}}\"", xaml);
    }

    [Fact]
    public void ImageCacheRows_KeepTheirColours()
    {
        AssertColor("#00BCD4", "SOURCE");
        AssertColor("#4CAF50", "CACHE");
        AssertColor(Red, "cache-failed");
        // exactly the three actions that dialog's producer emits
        var producer = RepoFile("MainWindow.axaml.cs");
        var emitted  = Regex.Matches(producer, "new Ingestion\\.IngestionOperation\\(fileName, \"([^\"]+)\"")
                            .Select(m => m.Groups[1].Value).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "CACHE", "SOURCE", "cache-failed" }, emitted);
    }

    [Fact]
    public void WriteVolumeToDiskRows_KeepTheirColours()
    {
        Assert.Contains("\"copy\" | \"verify\"", RepoFile("Volumes/WriteVolumeRow.cs"));   // the only two actions of that dialog
        AssertColor("#64B5F6", "copy");
        AssertColor("#CE93D8", "verify");
    }

    [Fact]
    public void EveryActionTheLogListsUnderFailures_IsRed()
    {
        // The text log's own FAILURES section is the application's definition of "this needs attention".
        var formatter = RepoFile("Ingestion/IngestionLogFormatter.cs");
        var failures  = Regex.Matches(formatter, "op\\.Action == \"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(15, failures.Count);
        Assert.All(failures, a => AssertColor(Red, a));
    }

    [Fact]
    public void EveryActionTheDialogFiltersAsFailed_IsRed()
    {
        // IngestionProgressDialog's "Failed" checkbox: four named actions + everything ending in "-failed".
        foreach (var action in new[] { "incomplete-skipped", "archive-collision", "archive-validation-blocked", "requirement-unsatisfiable" })
        {
            Assert.Contains($"action == \"{action}\"", RepoFile("Ingestion/IngestionProgressDialog.axaml.cs"));
            AssertColor(Red, action);
        }
    }

    [Fact]
    public void ConvertBack_IsStillUnsupported()
        => Assert.Throws<NotSupportedException>(() =>
               ActionColorConverter.Instance.ConvertBack(Brushes.Red, typeof(string), null, CultureInfo.InvariantCulture));
}
