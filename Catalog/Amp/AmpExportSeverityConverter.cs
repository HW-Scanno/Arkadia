using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia;

public sealed class AmpExportSeverityConverter : IValueConverter
{
    public static readonly AmpExportSeverityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is AmpExportPlanSeverity sev ? sev switch
        {
            AmpExportPlanSeverity.Error   => "#EF7070",
            AmpExportPlanSeverity.Warning => "#E0A040",
            AmpExportPlanSeverity.Info    => "#9FA4FF",
            _                             => "#888899",
        } : "#888899";
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
