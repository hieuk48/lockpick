using System.ComponentModel;
using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.Core.Display;
using Lockpick.Core.Monitoring;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Views;

/// <summary>
/// Shows the overlay panel on the game's picture (docs/DISPLAY.md requirements): finds the game window, follows its
/// position, size and monitor ten times a second, renders the panel (only when its values or the scale change) and
/// shows it through a plain layered window in the chosen corner, kept directly above the game. Hidden while the game is
/// minimized or hidden, or not in a game.
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>Gap between the panel and the picture's edge, in the overlay's own units (scaled with the panel).</summary>
    private const double Margin = 6;

    private readonly AppServices _services;
    private readonly OverlayViewModel _model;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private readonly OverlayWindow _host;
    private LayeredWindow? _window;
    private nint _game;
    private string? _installDirectory;
    private GameDisplaySettings? _display;
    private string _corner = string.Empty;
    private bool _dirty = true;
    private int _renderedDpi;
    private (int Width, int Height) _size;

    public OverlayController(AppServices services, OverlayViewModel model)
    {
        _services = services;
        _model = model;
        _log = services.LoggerFactory.CreateLogger("Overlay");
        _host = new OverlayWindow { DataContext = model };
        model.PropertyChanged += OnModelChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>The game window as last seen (null when there is none), for the Settings page.</summary>
    public GameWindowInfo? Game { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _model.PropertyChanged -= OnModelChanged;
        _window?.Dispose();
        _window = null;
        _host.Close();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverlayViewModel.Status))
        {
            _dirty = true;
        }
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

        if (string.IsNullOrEmpty(_model.Time))
        {
            Hide("Waiting for a game (the title screen and menus before a game show nothing)");
            return;
        }

        var directory = Path.GetDirectoryName(process.ExecutablePath);
        if (directory != _installDirectory)
        {
            _installDirectory = directory;
            _display = directory is null ? null : _services.Adapter.DisplaySettings(directory);
        }

        var picture = _display?.Picture(game.Client) ?? game.Client;
        Show(picture, game);
    }

    private void Show(PixelRect picture, GameWindowInfo game)
    {
        var dpiScale = Math.Max(1, game.Dpi) / 96.0;
        var before = _model.Scale;
        _model.SetPictureHeight(picture.Height / dpiScale);
        _window ??= new LayeredWindow("Lockpick overlay");
        var pixels = default(byte[]);
        if (_dirty || game.Dpi != _renderedDpi || !_window.IsVisible || _model.Scale != before)
        {
            (pixels, var width, var height) = _host.Render(_model.Scale, game.Dpi);
            _size = (width, height);
            _renderedDpi = game.Dpi;
            _dirty = false;
        }

        var margin = (int)Math.Round(Margin * _model.Scale * dpiScale);
        var left = _corner is OverlayCorners.TopLeft or OverlayCorners.BottomLeft;
        var top = _corner is not (OverlayCorners.BottomLeft or OverlayCorners.BottomRight);
        var x = left ? picture.X + margin : picture.Right - margin - _size.Width;
        var y = top ? picture.Y + margin : picture.Bottom - margin - _size.Height;
        if (pixels is not null)
        {
            _window.Show(x, y, _size.Width, _size.Height, pixels);
        }
        else if (_window.Bounds is var b && (b.X != x || b.Y != y))
        {
            _window.Move(x, y);
        }

        GameWindows.KeepAbove(_window.Handle, game.Handle);
        _model.Status = "Showing over the game.";
    }

    private void Hide(string reason)
    {
        _window?.Hide();
        _model.Status = reason;
    }
}
