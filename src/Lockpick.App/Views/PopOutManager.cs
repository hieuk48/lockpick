using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lockpick.App.Controls;
using Lockpick.App.Services;

namespace Lockpick.App.Views;

/// <summary>
/// Overlay widgets in their own windows (roadmap: widgets off the game too): movable, resizable, on any monitor, on a
/// plain dark or a green (chroma key) background for OBS's window capture, always on top if the player wants. Each
/// window's place and options are remembered, and the ones open when Lockpick closed open again.
/// </summary>
public sealed class PopOutManager : IDisposable
{
    public static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#0B0F0E"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#00B140"));

    private readonly AppServices _services;
    private readonly WidgetRenderer _renderer;
    private readonly Dictionary<string, Window> _open = [];
    private bool _closing;

    public PopOutManager(AppServices services, WidgetRenderer renderer)
    {
        _services = services;
        _renderer = renderer;
        services.WidgetWindows.ToggleRequested += OnToggleRequested;
    }

    private void OnToggleRequested(object? sender, string id) => Toggle(id);

    public void Toggle(string id)
    {
        if (_open.TryGetValue(id, out var window))
        {
            window.Close();
        }
        else
        {
            Open(id, activate: true);
        }
    }

    /// <summary>Opens the pop-outs that were open when Lockpick last closed (without taking focus).</summary>
    public void RestoreOpen()
    {
        foreach (var (id, options) in _services.Settings.Current.PopOuts)
        {
            if (options.Open && _renderer.Ids.Contains(id))
            {
                Open(id, activate: false);
            }
        }
    }

    public void Dispose()
    {
        _services.WidgetWindows.ToggleRequested -= OnToggleRequested;
        _closing = true; // closing with Lockpick: they open again next time
        foreach (var window in _open.Values.ToList())
        {
            window.Close();
        }

        _open.Clear();
    }

    private void Open(string id, bool activate)
    {
        var options = _services.Settings.Current.PopOuts.GetValueOrDefault(id) ?? new PopOutOptions();
        var name = OverlayLayout.Widgets.FirstOrDefault(w => w.Id == id).Name ?? id;
        var view = new WidgetView { WidgetId = id, Margin = new Thickness(6), EmptyBrush = new SolidColorBrush(Color.Parse("#8FA39B")) };
        var window = new Window
        {
            Title = $"Lockpick — {name}",
            Width = options.Width,
            Height = options.Height,
            MinWidth = 120,
            MinHeight = 80,
            Background = options.Green ? Green : Dark,
            Topmost = options.Topmost,
            ShowActivated = activate,
            ShowInTaskbar = true,
            Content = view,
        };
        if (options.X is { } x && options.Y is { } y)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }

        var green = new MenuItem { Header = "Green background (for OBS's chroma key)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.Green };
        green.Click += (_, _) =>
        {
            window.Background = green.IsChecked ? Green : Dark;
            Save(id, window, open: true, green.IsChecked, window.Topmost);
        };
        var top = new MenuItem { Header = "Always on top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.Topmost };
        top.Click += (_, _) =>
        {
            window.Topmost = top.IsChecked;
            Save(id, window, open: true, window.Background == Green, top.IsChecked);
        };
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => window.Close();
        window.ContextMenu = new ContextMenu { ItemsSource = new object[] { green, top, new Separator(), close } };
        window.Closing += (_, _) => Save(id, window, open: _closing, window.Background == Green, window.Topmost);
        window.Closed += (_, _) =>
        {
            _open.Remove(id);
            _services.WidgetWindows.SetOpen(id, false);
        };
        _open[id] = window;
        window.Show();
        Save(id, window, open: true, options.Green, options.Topmost);
        _services.WidgetWindows.SetOpen(id, true);
    }

    private void Save(string id, Window window, bool open, bool green, bool topmost)
    {
        var popOuts = new Dictionary<string, PopOutOptions>(_services.Settings.Current.PopOuts)
        {
            [id] = new PopOutOptions
            {
                Open = open,
                X = window.Position.X,
                Y = window.Position.Y,
                Width = window.Width is > 0 and var w ? w : window.Bounds.Width,
                Height = window.Height is > 0 and var h ? h : window.Bounds.Height,
                Green = green,
                Topmost = topmost,
            },
        };
        try
        {
            _services.Settings.Save(_services.Settings.Current with { PopOuts = popOuts });
        }
        catch (IOException)
        {
            // Remembering the window is a convenience; a locked settings file must not break it.
        }
    }
}
