using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia;

/// <summary>Converts an ingestion Action string to a foreground brush.</summary>
public sealed class ActionColorConverter : IValueConverter
{
    public static readonly ActionColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var action = value as string ?? "";
        var hex = action switch
        {
            "copy"   => "#64B5F6",
            "source" => "#81C784",
            "delete" => "#9E9E9E",
            "skip"   => "#FFB74D",
            "hash"   => "#B39DDB",
            _        => action.EndsWith("-failed", StringComparison.Ordinal) ? "#E57373" : "#FFB74D",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
