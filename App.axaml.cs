using System;
using System.Threading.Tasks;
using Arkadia.Startup;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Arkadia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (splashPath, soundPath) = StartupBootstrap.Resolve();

            StartupSoundPlayer.TryPlay(soundPath);

            var main = new MainWindow();
            desktop.MainWindow = main;

            if (splashPath is not null)
            {
                var splash = new SplashWindow(splashPath);
                if (splash.HasImage)
                {
                    var mainReady = new TaskCompletionSource();

                    // Self-unsubscribing handler — signals readiness exactly once
                    main.Opened += OnMainOpened;
                    void OnMainOpened(object? s, EventArgs e)
                    {
                        main.Opened -= OnMainOpened;
                        mainReady.TrySetResult();
                    }

                    // Fire-and-forget; RunAsync catches all failures internally
                    _ = splash.RunAsync(mainReady.Task);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
