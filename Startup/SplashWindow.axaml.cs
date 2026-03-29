using System;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Arkadia.Startup;

/// <summary>
/// Borderless, centred splash window that displays a single .png.
/// Call <see cref="RunAsync"/> to show with fade-in, hold for the minimum visible duration,
/// then fade out. The window is NOT closed by RunAsync — the caller must close it after
/// ensuring another window is already shown (to prevent a zero-window shutdown gap).
/// </summary>
public partial class SplashWindow : Window
{
    public static readonly TimeSpan FadeInDuration     = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MinVisibleDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FadeOutDuration    = TimeSpan.FromMilliseconds(250);

    public bool HasImage { get; private set; }

    // Parameterless ctor satisfies Avalonia's XAML runtime loader requirement (AVLN3001).
    public SplashWindow() : this(string.Empty) { }

    public SplashWindow(string imagePath)
    {
        InitializeComponent();
        Opacity = 0.0; // start transparent; RunAsync fades in
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
    /// Shows the splash, fades in, waits for the minimum visible duration, then fades out.
    /// Returns when the visual sequence is complete. Does NOT close the window — the caller
    /// must call <see cref="Window.Close"/> after showing the next window to avoid a
    /// zero-window shutdown gap.
    /// </summary>
    public async Task RunAsync()
    {
        // Start the minimum-duration timer immediately so fade-in time counts toward it
        var minDelay = Task.Delay(MinVisibleDuration);
        try
        {
            Show();
            await FadeInAsync();
            await minDelay;
            await FadeOutAsync();
        }
        catch
        {
            // Animation or coordination failure — visual sequence aborted; caller proceeds
        }
        // Intentionally no Close() here — see summary.
    }

    private Task FadeInAsync()  => FadeOpacityAsync(0.0, 1.0, FadeInDuration);
    private Task FadeOutAsync() => FadeOpacityAsync(1.0, 0.0, FadeOutDuration);

    private async Task FadeOpacityAsync(double from, double to, TimeSpan duration)
    {
        Opacity = from;
        try
        {
            var animation = new Animation
            {
                Duration = duration,
                FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, from) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, to)   } },
                },
            };
            await animation.RunAsync(this);
        }
        catch
        {
            // Animation failure — snap to target opacity and continue
        }
        Opacity = to; // ensure final value regardless of animation outcome
    }
}
