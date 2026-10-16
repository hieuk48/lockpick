using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.App.Views;

namespace Lockpick.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed from the desktop lifetime's ShutdownRequested handler.")]
public partial class App : Application
{
    private AppServices? _services;
    private MainViewModel? _main;
    private OverlayController? _overlay;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Create(desktop.Args ?? []);
            _main = new MainViewModel(_services);
            desktop.MainWindow = new MainWindow { DataContext = _main };

            // The overlay is a window of its own; the app still ends with the main window.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _overlay = new OverlayController(_services, _main.Overlay);
            _services.Source.Start();
            _services.Updates.StartBackgroundChecks();
            desktop.ShutdownRequested += (_, _) => Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        _overlay?.Dispose();
        _overlay = null;
        _main?.Dispose();

        // Stop the monitor (closing the process handle) before the logger is torn down.
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services = null;
    }
}
