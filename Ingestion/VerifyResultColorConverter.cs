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
            "VERIFIED"    => "#81C784",
            "MISSING"     => "#EF5350",
            "MISMATCH"    => "#FF7043",
            "UNEXPECTED"  => "#FFB74D",
            "SKIPPED"     => "#555566",
            "found-file"  => "#444455",  // neutral dim — scan discovery, not a verify result
            _             => "#888899",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
