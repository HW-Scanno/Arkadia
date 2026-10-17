using Arkadia.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Arkadia.Systems;

/// <summary>
/// The ONE definition of the compact pill used for Arkadia status badges (STAGING, NEEDS VOLUME, READY TO MOVE,
/// ARCHIVED and LOADING). Everything about its shape — corner radius, padding, border thickness, typography,
/// alignment — lives here, so every badge is identical except for its colour and text.
/// </summary>
public static class StatusBadge
{
    /// <summary>Metrics shared by every badge (exposed so tests can pin them).</summary>
    public const double CornerRadius = 3, BorderThickness = 1, FontSize = 9, PaddingX = 6, PaddingY = 1;

    public static Border Create(string text, string hex, string? tooltip)
    {
        var color = Color.Parse(hex);
        var pill = new Border
        {
            Background        = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
            BorderBrush       = new SolidColorBrush(color),
            BorderThickness   = new Thickness(BorderThickness),
            CornerRadius      = new CornerRadius(CornerRadius),
            Padding           = new Thickness(PaddingX, PaddingY),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child             = new TextBlock
            {
                Text              = text,
                FontSize          = FontSize,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(color),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        if (tooltip is not null) ToolTip.SetTip(pill, tooltip);
        return pill;
    }

    /// <summary>
    /// The LOADING badge: the standard pill in light neutral, whose whole surface (fill, border, text) blinks in sync
    /// with the shared <see cref="ActivityClock"/> — opacity only, so its size and position never change.
    /// </summary>
    public static Border CreateLoading(ActivityClock? clock = null)
    {
        var pill = Create(StorageBadgePolicy.LoadingLabel, StorageBadgePolicy.LoadingColor, StorageBadgePolicy.LoadingTooltip);
        ActivityBlink.Attach(pill, clock);
        return pill;
    }
}
