using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Arkadia;

public sealed class ToUpperConverter : IValueConverter
{
    public static readonly ToUpperConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToUpperInvariant() ?? value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
