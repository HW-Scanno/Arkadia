using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// Converts an ingestion Action string to a foreground brush.
///
/// <para>One colour per meaning, so the outcome of a file can be read from the colour of its last rows without
/// reading every label. Every colour is one the application already used; an action that is not listed keeps the
/// historical fallback (amber, or red when its name ends in <c>-failed</c>).</para>
///
/// <para>The same converter also colours the Image Cache rows (<c>SOURCE</c> / <c>CACHE</c>) and the Write Volume to
/// Disk rows (<c>copy</c> / <c>verify</c>); those entries are unchanged.</para>
/// </summary>
public sealed class ActionColorConverter : IValueConverter
{
    public static readonly ActionColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var action = value as string ?? "";
        var hex = action switch
        {
            // Hashing and analysis — purple
            "hash" or "deep-hash" or "deep-hash-physical-match" or "deep-hash-not-applicable"
                or "dat-hash-enriched" or "rvz-verified" => "#B39DDB",

            // Staging and preparatory operations — blue
            "copy" or "stage-moved" or "empty-synthesized" or "extract-ok" or "staging-resumed" => "#64B5F6",

            // Ready or already satisfied — green ("source" is the former name of "release-input-assembled")
            "source" or "release-input-assembled" or "already-present" => "#81C784",

            // Successfully acquired artifacts — cyan
            "transform" or "derived-committed" or "rvz-adopted-moved" or "rvz-adopted-copied" => "#26C6DA",

            // Unwanted exclusions and policy decisions — grey (the Unwanted grey of the Library)
            "unwanted-classified" or "unwanted-moved" or "stale-staging-unwanted-moved"
                or "stale-source-unwanted-moved" or "discarded-by-strategy" => "#9E9E9E",

            // Cleanup and redundant files — muted grey
            "delete" or "duplicate-deleted" or "rvz-already-present" or "archive-deleted"
                or "stale-cue-only-staging-moved" => "#888899",

            // Warnings and skipped operations — amber
            "skip" or "rvz-no-match" or "rvz-ambiguous" or "rvz-logical-conflict" or "rvz-unsupported-release"
                or "dat-hash-conflict" or "extract-skipped" or "deep-hash-unavailable"
                or "present-without-artifact" or "rebuild-required" or "stale-artifact-overwritten"
                or "archive-preserved" => "#FFB74D",

            // Failures and blocked operations that do not end in "-failed" — red
            "transform-config-error" or "leaf-finalization-error" or "cleanup-error" or "incomplete-skipped"
                or "archive-collision" or "archive-validation-blocked" or "requirement-unsatisfiable"
                or "rvz-existing-artifact-conflict" or "rvz-target-exists" => "#E57373",

            "verify"    => "#CE93D8",   // Write Volume to Disk: verification row
            "SOURCE"    => "#00BCD4",   // image cache: source master row
            "CACHE"     => "#4CAF50",   // image cache: cached variant row
            _           => action.EndsWith("-failed", StringComparison.Ordinal) ? "#E57373" : "#FFB74D",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
