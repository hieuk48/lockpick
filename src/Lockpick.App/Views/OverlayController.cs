using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.Core.Display;
using Lockpick.Core.Monitoring;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Views;

/// <summary>
/// Keeps the overlay window on the game's picture (docs/DISPLAY.md requirements): finds the game window, follows its
/// position, size and monitor ten times a second, and hides the overlay while the game is minimized or hidden, and in
/// exclusive fullscreen: there the game puts itself back above the overlay within 50–200 ms of every raise (live
/// 2026-10-15; docs/DISPLAY.md), so it would only flicker.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayViewModel _model;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private OverlayWindow? _window;
    private nint _game;
    private string? _installDirectory;
    private GameDisplaySettings? _display;
    private string _corner = string.Empty;

    public OverlayController(AppServices services, OverlayViewModel model)
    {
        _services = services;
        _model = model;
        _log = services.LoggerFactory.CreateLogger("Overlay");
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>The game window as last seen (null when there is none), for the Settings page.</summary>
    public GameWindowInfo? Game { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _window?.Close();
        _window = null;
    }

    private void Tick()
    {
        var settings = _services.Settings.Current;
        if (settings.OverlayCorner != _corner)
        {
            _corner = settings.OverlayCorner;
            _model.SetCorner(_corner);
        }

        var status = _services.Source.Status;
        if (status.State != ConnectionState.Attached || status.Process is not { } process)
        {
            Game = null;
            _game = 0;
            Hide("Resident Evil isn't running");
            return;
        }

        _game = GameWindows.Find(process.ProcessId, _services.Adapter.WindowClass, _game);
        Game = GameWindows.Read(_game);
        if (!settings.OverlayEnabled)
        {
            Hide("Off");
            return;
        }

        if (Game is not { } game || !game.CanOverlay)
        {
            Hide("The game window is minimized or hidden");
            return;
        }

        if (game.Mode == GameWindowMode.ExclusiveFullscreen)
        {
            Hide("The game is in exclusive fullscreen, where it covers the overlay — switch it to borderless (windowed at desktop size)");
            return;
        }

        var directory = Path.GetDirectoryName(process.ExecutablePath);
        if (directory != _installDirectory)
        {
            _installDirectory = directory;
            _display = directory is null ? null : _services.Adapter.DisplaySettings(directory);
        }

        var picture = _display?.Picture(game.Client) ?? game.Client;
        Show(picture, game.Dpi);
        if (_window?.Handle is { } overlay and not 0)
        {
            GameWindows.KeepAbove(overlay, game.Handle);
        }
    }

    private void Show(PixelRect picture, int dpi)
    {
        if (_window is null)
        {
            _window = new OverlayWindow { DataContext = _model };
            _window.Show();
        }
        else if (!_window.IsVisible)
        {
            _window.Show();
        }

        // Compared with where the window really is: moving to a monitor with another scale makes Windows resize it for
        // the new DPI, and the next tick puts it back on the picture.
        if (_window.Handle != 0 && GameWindows.Bounds(_window.Handle) != picture && GameWindows.Place(_window.Handle, picture))
        {
            _model.SetPictureHeight(picture.Height / Math.Max(1, dpi / 96.0));
            _log.LogDebug("Overlay on the game picture {Picture} (DPI {Dpi})", picture, dpi);
        }

        _model.Status = _window.ClickThrough ? "Showing over the game." : "Showing over the game (click-through couldn't be set).";
    }

    private void Hide(string reason)
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }

        _model.Status = reason;
    }
}
