using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia;

/// <summary>Maps a VerifyRow.Result string to a foreground color.</summary>
public sealed class VerifyResultColorConverter : IValueConverter
{
    public static readonly VerifyResultColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = (value as string) switch
        {
            // Legacy DAT-line verify results (uppercase)
            "VERIFIED"    => "#81C784",
            "MISSING"     => "#EF5350",
            "MISMATCH"    => "#FF7043",
            "UNEXPECTED"  => "#FFB74D",
            "SKIPPED"     => "#555566",

            // Volume full-scan — neutral progress (not a result)
            "found-file"  => "#444455",  // dim — scan discovery
            "hashing"     => "#5C6BC0",  // neutral blue-gray — computing SHA1
            "classified"  => "#666677",  // neutral gray — classification determined

            // Volume full-scan — real results / recovery outcomes
            "verify-ok"               => "#81C784",  // green — present & hash OK
            "missing"                 => "#EF5350",  // red
            "collision"               => "#EF5350",  // red — move blocked
            "misplaced-found"         => "#FFB74D",  // amber — needs recovery
            "misplaced-restored"      => "#64B5F6",  // blue — recovered to root
            "unwanted-found"          => "#FFB74D",  // amber
            "unwanted-moved"          => "#BA68C8",  // purple — moved to managed folder
            "known-unexpected-found"  => "#FFB74D",  // amber
            "known-unexpected-moved"  => "#BA68C8",  // purple
            "unknown-found"           => "#FFB74D",  // amber
            "unknown-moved"           => "#BA68C8",  // purple

            // Volume fillback actions
            "fillback-moving"                  => "#64B5F6",  // blue — move in progress
            "fillback-copying"                 => "#64B5F6",  // blue — copy in progress
            "fillback-verifying"               => "#CE93D8",  // purple — hash verification
            "fillback-deleting-source"         => "#9E9E9E",  // gray — deleting source
            "fillback-moved"                   => "#81C784",  // green — success (same-disk)
            "fillback-copied-verified-deleted" => "#81C784",  // green — success (cross-disk)
            "fillback-skip"                    => "#555566",  // dim — skipped
            "fillback-error"                   => "#EF5350",  // red — failure
            "usage-refreshed"                  => "#444455",  // dim — neutral event

            _             => "#888899",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
