using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.Core.Display;
using Lockpick.Core.Monitoring;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Views;

/// <summary>
/// Shows the overlay's widgets on the game's picture (docs/DISPLAY.md requirements): finds the game window, follows its
/// position, size and monitor ten times a second, renders each widget (only when its values or the scale change) and
/// shows it through its own plain layered window in its corner, kept directly above the game. Widgets sharing a corner
/// stack away from it. Hidden while the game is minimized or hidden, or not in a game.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayViewModel _model;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private readonly OverlayWindow _host;
    private readonly List<Widget> _widgets;
    private nint _game;
    private string? _installDirectory;
    private GameDisplaySettings? _display;

    public OverlayController(AppServices services, OverlayViewModel model)
    {
        _services = services;
        _model = model;
        _log = services.LoggerFactory.CreateLogger("Overlay");
        _host = new OverlayWindow { DataContext = model };
        _widgets =
        [
            new Widget(OverlayLayout.Status, _host.StatusPanel, () => true, () => false),
            new Widget(OverlayLayout.Minimap, _host.MinimapPanel, () => model.HasMinimap, () => model.MinimapFull),
            new Widget(OverlayLayout.Enemies, _host.EnemiesPanel, () => model.Enemies.Count > 0, () => false),
            new Widget(OverlayLayout.Splits, _host.SplitsPanel, () => model.SplitRows.Count > 0, () => false),
        ];
        _host.ShowEnemies(model.Enemies);
        _host.ShowSplits(model);
        model.PropertyChanged += OnModelChanged;
        model.Map.PropertyChanged += OnMapChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>The game window as last seen (null when there is none), for the Settings page.</summary>
    public GameWindowInfo? Game { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _model.PropertyChanged -= OnModelChanged;
        _model.Map.PropertyChanged -= OnMapChanged;
        _model.Map.KeepUpdating = false;
        foreach (var widget in _widgets)
        {
            widget.Window?.Dispose();
            widget.Window = null;
        }

        _host.Close();
    }

    private Widget Find(string id) => _widgets.First(w => w.Id == id);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.Status))
        {
            return;
        }

        if (e.PropertyName is nameof(OverlayViewModel.SplitRows) or nameof(OverlayViewModel.RunClock) or nameof(OverlayViewModel.RunTitle)
            or nameof(OverlayViewModel.RunComparison) or nameof(OverlayViewModel.RunBehind))
        {
            _host.ShowSplits(_model);
            Find(OverlayLayout.Splits).Dirty = true;
            return;
        }

        if (e.PropertyName == nameof(OverlayViewModel.Enemies))
        {
            _host.ShowEnemies(_model.Enemies);
            Find(OverlayLayout.Enemies).Dirty = true;
            return;
        }

        Find(e.PropertyName?.StartsWith("Minimap", StringComparison.Ordinal) == true ? OverlayLayout.Minimap : OverlayLayout.Status).Dirty = true;
    }

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e) => Find(OverlayLayout.Minimap).Dirty = true;

    private void Tick()
    {
        var settings = _services.Settings.Current;
        _model.UseMapSettings(settings.MapOverlay);
        var status = _services.Source.Status;
        var minimapOn = settings.OverlayEnabled && OverlayLayout.Corner(settings, OverlayLayout.Minimap) != OverlayLayout.Off;
        _model.Map.KeepUpdating = minimapOn && status.State == ConnectionState.Attached;
        if (status.State != ConnectionState.Attached || status.Process is not { } process)
        {
            Game = null;
            _game = 0;
            HideAll("Resident Evil isn't running");
            return;
        }

        _game = GameWindows.Find(process.ProcessId, _services.Adapter.WindowClass, _game);
        Game = GameWindows.Read(_game);
        if (!settings.OverlayEnabled)
        {
            HideAll("Off");
            return;
        }

        if (Game is not { } game || !game.CanOverlay)
        {
            HideAll("The game window is minimized or hidden");
            return;
        }

        if (string.IsNullOrEmpty(_model.Time))
        {
            HideAll("Waiting for a game (the title screen and menus before a game show nothing)");
            return;
        }

        var directory = Path.GetDirectoryName(process.ExecutablePath);
        if (directory != _installDirectory)
        {
            _installDirectory = directory;
            _display = directory is null ? null : _services.Adapter.DisplaySettings(directory);
        }

        var picture = _display?.Picture(game.Client) ?? game.Client;
        Show(picture, game, settings);
    }

    private void Show(PixelRect picture, GameWindowInfo game, AppSettings settings)
    {
        var dpiScale = Math.Max(1, game.Dpi) / 96.0;
        _model.SetPicture(picture.Width / dpiScale, picture.Height / dpiScale);
        var margin = (int)Math.Round(OverlayViewModel.Margin * _model.Scale * dpiScale);
        var shown = 0;
        foreach (var corner in OverlayCorners.All)
        {
            var left = corner is OverlayCorners.TopLeft or OverlayCorners.BottomLeft;
            var top = corner is OverlayCorners.TopLeft or OverlayCorners.TopRight;
            var offset = margin; // from the corner, along the picture's height
            foreach (var widget in _widgets.Where(w => OverlayLayout.Corner(settings, w.Id) == corner))
            {
                if (!widget.HasContent())
                {
                    widget.Window?.Hide();
                    continue;
                }

                var pixels = Render(widget, game);
                var (w, h) = widget.Size;
                int x, y;
                if (widget.Full())
                {
                    // Over the whole picture (its size follows the picture), out of the corner's stack.
                    (x, y) = (picture.X + margin, picture.Y + margin);
                }
                else
                {
                    x = left ? picture.X + margin : picture.Right - margin - w;
                    y = top ? picture.Y + offset : picture.Bottom - offset - h;
                    offset += h + margin;
                }

                if (pixels is not null)
                {
                    widget.Window!.Show(x, y, w, h, pixels);
                }
                else if (widget.Window!.Bounds is var b && (b.X != x || b.Y != y))
                {
                    widget.Window.Move(x, y);
                }

                shown++;
            }
        }

        foreach (var widget in _widgets.Where(w => OverlayLayout.Corner(settings, w.Id) == OverlayLayout.Off))
        {
            widget.Window?.Hide();
        }

        var visible = _widgets.Where(w => w.Window is { IsVisible: true }).ToList();
        var windows = visible.Select(w => w.Window!.Handle).ToList();
        foreach (var handle in windows)
        {
            GameWindows.KeepAbove(handle, game.Handle, windows);
        }

        // The other widgets go above a full-screen one (the time and HP stay readable on top of the map).
        foreach (var full in visible.Where(w => w.Full()))
        {
            foreach (var other in visible.Where(w => !w.Full()))
            {
                GameWindows.PlaceAbove(other.Window!.Handle, full.Window!.Handle);
            }
        }

        _model.Status = shown > 0 ? "Showing over the game." : "On, with every widget off or empty.";
    }

    /// <summary>Renders the widget when its look changed (or the scale or monitor); null when the last picture still holds.</summary>
    private byte[]? Render(Widget widget, GameWindowInfo game)
    {
        widget.Window ??= new LayeredWindow($"Lockpick overlay ({widget.Id})");
        if (!widget.Dirty && game.Dpi == widget.RenderedDpi && _model.Scale == widget.RenderedScale && widget.Window.IsVisible)
        {
            return null;
        }

        var (pixels, width, height) = OverlayWindow.Render(widget.Panel, _model.Scale, game.Dpi);
        widget.Size = (width, height);
        widget.RenderedDpi = game.Dpi;
        widget.RenderedScale = _model.Scale;
        widget.Dirty = false;
        return pixels;
    }

    private void HideAll(string reason)
    {
        foreach (var widget in _widgets)
        {
            widget.Window?.Hide();
        }

        _model.Status = reason;
    }

    /// <summary>One widget: its panel in the hidden host, and the layered window that shows it over the game.</summary>
    private sealed class Widget(string id, LayoutTransformControl panel, Func<bool> hasContent, Func<bool> full)
    {
        public string Id { get; } = id;

        public LayoutTransformControl Panel { get; } = panel;

        public Func<bool> HasContent { get; } = hasContent;

        /// <summary>Covers the game's picture now instead of sitting in its corner.</summary>
        public Func<bool> Full { get; } = full;

        public LayeredWindow? Window { get; set; }

        public bool Dirty { get; set; } = true;

        public int RenderedDpi { get; set; }

        public double RenderedScale { get; set; }

        public (int Width, int Height) Size { get; set; }
    }
}
