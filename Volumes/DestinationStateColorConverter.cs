using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia.Volumes;

/// <summary>Converts a DestinationState to a foreground brush for the Status column.</summary>
public sealed class DestinationStateColorConverter : IValueConverter
{
    public static readonly DestinationStateColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is DestinationState state ? state switch
        {
            DestinationState.Ready              => "#81C784",
            DestinationState.NotEnoughFreeSpace => "#FFB74D",
            DestinationState.NotMounted         => "#666677",
            _                                   => "#AAAACC",
        } : "#AAAACC";
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
