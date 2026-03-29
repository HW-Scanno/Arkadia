using System;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Arkadia.Startup;

/// <summary>
/// Borderless, centred splash window that displays a single .png.
/// Call <see cref="RunAsync"/> to show with fade-in, enforce a minimum visible duration,
/// then fade out and close automatically.
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
    /// Shows the splash, fades in, waits until both the main window is ready and the
    /// minimum visible duration has elapsed, then fades out and closes.
    /// Intended as fire-and-forget from App startup; all failures degrade gracefully.
    /// </summary>
    public async Task RunAsync(Task mainReady)
    {
        // Start the minimum-duration timer immediately so fade-in time counts toward it
        var minDelay = Task.Delay(MinVisibleDuration);
        try
        {
            Show();
            await FadeInAsync();
            await Task.WhenAll(mainReady, minDelay);
            await FadeOutAsync();
        }
        catch
        {
            // Animation or coordination failure — proceed to close
        }
        finally
        {
            try { Close(); } catch { }
        }
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
