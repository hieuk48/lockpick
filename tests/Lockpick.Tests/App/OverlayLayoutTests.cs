using Lockpick.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.App;

public sealed class OverlayLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Widgets_start_in_their_default_corners() =>
        Assert.Equal(OverlayCorners.BottomRight, OverlayLayout.Corner(new AppSettings(), OverlayLayout.Minimap));

    [Fact]
    public void The_time_panel_keeps_the_corner_chosen_before_widgets() =>
        Assert.Equal(OverlayCorners.BottomLeft, OverlayLayout.Corner(new AppSettings { OverlayCorner = OverlayCorners.BottomLeft }, OverlayLayout.Status));

    [Fact]
    public void A_widget_s_own_corner_wins_and_off_is_kept()
    {
        var settings = new AppSettings
        {
            OverlayCorner = OverlayCorners.BottomLeft,
            OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Status] = OverlayCorners.TopLeft, [OverlayLayout.Minimap] = OverlayLayout.Off },
        };
        Assert.Equal(OverlayCorners.TopLeft, OverlayLayout.Corner(settings, OverlayLayout.Status));
        Assert.Equal(OverlayLayout.Off, OverlayLayout.Corner(settings, OverlayLayout.Minimap));
    }

    [Fact]
    public void An_unknown_corner_falls_back_to_the_default()
    {
        var settings = new AppSettings { OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Minimap] = "Middle" } };
        Assert.Equal(OverlayCorners.BottomRight, OverlayLayout.Corner(settings, OverlayLayout.Minimap));
    }

    [Fact]
    public void Map_options_survive_a_save_and_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        var options = new MapOverlaySettings { Views = [MapViews.Hidden, MapViews.RoomSeeThrough], Reach = 9000, Size = 170, SeeThroughOpacity = 40, Names = false };
        new SettingsStore(path, NullLogger.Instance).Save(new AppSettings { MapOverlay = options });

        var loaded = new SettingsStore(path, NullLogger.Instance).Load().MapOverlay;
        Assert.Equal(options.Views, loaded.Views);
        Assert.Equal((9000, 170, 40, false), (loaded.Reach, loaded.Size, loaded.SeeThroughOpacity, loaded.Names));
    }

    [Fact]
    public void Settings_from_before_the_map_options_get_the_defaults()
    {
        var path = Path.Combine(_folder, "settings.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{\"overlayEnabled\": true, \"overlayCorner\": \"Top left\"}");
        var loaded = new SettingsStore(path, NullLogger.Instance).Load();
        Assert.Equal([MapViews.Around, MapViews.Room, MapViews.RoomSeeThrough, MapViews.Floor], loaded.MapOverlay.Views);
        Assert.Equal(OverlayCorners.TopLeft, OverlayLayout.Corner(loaded, OverlayLayout.Status));
    }

    [Fact]
    public void Pop_out_windows_and_the_stream_page_survive_a_save_and_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        var popOuts = new Dictionary<string, PopOutOptions>
        {
            [OverlayLayout.Splits] = new() { Open = true, X = -1900, Y = 40, Width = 300, Height = 560, Green = true, Topmost = true },
        };
        new SettingsStore(path, NullLogger.Instance).Save(new AppSettings { PopOuts = popOuts, StreamWidgets = ["minimap:Large", "splits:Small"] });

        var loaded = new SettingsStore(path, NullLogger.Instance).Load();
        Assert.Equal(popOuts[OverlayLayout.Splits], loaded.PopOuts[OverlayLayout.Splits]);
        Assert.Equal(["minimap:Large", "splits:Small"], loaded.StreamWidgets);
        Assert.Null(new AppSettings().StreamWidgets); // none saved: the page's default layout
    }

    [Fact]
    public void Widget_windows_report_each_change_once()
    {
        var windows = new WidgetWindows();
        var changes = 0;
        string? asked = null;
        windows.Changed += (_, _) => changes++;
        windows.ToggleRequested += (_, id) => asked = id;

        windows.Toggle(OverlayLayout.Minimap);
        windows.SetOpen(OverlayLayout.Minimap, true);
        windows.SetOpen(OverlayLayout.Minimap, true);
        Assert.Equal(OverlayLayout.Minimap, asked);
        Assert.True(windows.IsOpen(OverlayLayout.Minimap));
        windows.SetOpen(OverlayLayout.Minimap, false);
        Assert.False(windows.IsOpen(OverlayLayout.Minimap));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Only_the_room_views_cover_the_picture()
    {
        Assert.True(MapViews.IsFull(MapViews.Room));
        Assert.True(MapViews.IsFull(MapViews.RoomSeeThrough));
        Assert.False(MapViews.IsFull(MapViews.Around));
        Assert.False(MapViews.IsFull(MapViews.Hidden));
    }
}
