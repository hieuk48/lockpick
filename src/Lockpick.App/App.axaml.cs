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
    private WidgetRenderer? _widgets;
    private PopOutManager? _popOuts;

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
            _widgets = new WidgetRenderer(_services, _main.Overlay);
            WidgetRenderer.Current = _widgets;
            _services.DrawWidget = _widgets.ForBrowser;
            _overlay = new OverlayController(_services, _main.Overlay, _widgets);
            FocusGuard.GameHoldsTheScreen = () => _overlay?.Game is { Mode: Core.Display.GameWindowMode.ExclusiveFullscreen, Foreground: true };
            _popOuts = new PopOutManager(_services, _widgets);
            _popOuts.RestoreOpen();
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
        _popOuts?.Dispose();
        _popOuts = null;
        _widgets?.Dispose();
        _widgets = null;
        _main?.Dispose();

        // Stop the monitor (closing the process handle) before the logger is torn down. On the thread pool: this
        // thread waits, and an await inside (the API's shutdown) that came back to it would hang the app on close.
        if (_services is { } services)
        {
            Task.Run(() => services.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        _services = null;
    }
}
