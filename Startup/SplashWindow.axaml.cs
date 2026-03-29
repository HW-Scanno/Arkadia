using System;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Arkadia.Startup;

/// <summary>
/// Borderless, centred splash window that displays a single .png.
/// Opacity is animated on the root content panel (SplashRoot), NOT on the Window itself,
/// to avoid DWM/compositor flicker from window-level transparency changes.
/// The caller must close the window after showing the next window to prevent a
/// zero-window shutdown gap.
/// </summary>
public partial class SplashWindow : Window
{
    public static readonly TimeSpan FadeInDuration     = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan MinVisibleDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FadeOutDuration    = TimeSpan.FromMilliseconds(500);

    public bool HasImage { get; private set; }

    // Parameterless ctor satisfies Avalonia's XAML runtime loader requirement (AVLN3001).
    public SplashWindow() : this(string.Empty) { }

    public SplashWindow(string imagePath)
    {
        InitializeComponent();
        // Window stays fully opaque; content panel starts transparent
        SplashRoot.Opacity = 0.0;
        try
        {
            SplashImage.Source = new Bitmap(imagePath);
            HasImage = true;
        }
        catch
        {
            // Invalid or unreadable image — caller checks HasImage before calling RunAsync
        }
    }

    /// <summary>
    /// Shows the splash, fades in the content panel, waits for the minimum visible duration,
    /// then fades out the content panel. Returns when the visual sequence is complete.
    /// Does NOT close the window — the caller must close it after showing the next window.
    /// </summary>
    public async Task RunAsync()
    {
        // Start the minimum-duration timer immediately so fade-in time counts toward it
        var minDelay = Task.Delay(MinVisibleDuration);
        try
        {
            Show();
            await FadeContentAsync(0.0, 1.0, FadeInDuration);
            await minDelay;
            await FadeContentAsync(1.0, 0.0, FadeOutDuration);
        }
        catch
        {
            // Animation or coordination failure — visual sequence aborted; caller proceeds
        }
        // Intentionally no Close() here — caller owns window lifetime to prevent shutdown gap
    }

    private async Task FadeContentAsync(double from, double to, TimeSpan duration)
    {
        SplashRoot.Opacity = from;
        try
        {
            var animation = new Animation
            {
                Duration = duration,
                FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Avalonia.Controls.Grid.OpacityProperty, from) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Avalonia.Controls.Grid.OpacityProperty, to)   } },
                },
            };
            await animation.RunAsync(SplashRoot);
        }
        catch
        {
            // Animation failure — snap to target opacity and continue
        }
        SplashRoot.Opacity = to;
    }
}
