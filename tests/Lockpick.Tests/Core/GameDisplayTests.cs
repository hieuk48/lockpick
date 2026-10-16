using Lockpick.Core.Display;

namespace Lockpick.Tests.Core;

/// <summary>Where the picture sits and which mode a window is in — cases measured live (docs/DISPLAY.md).</summary>
public sealed class GameDisplayTests
{
    private static readonly GameDisplaySettings Fit = new(PictureScaling.Fit, 640, 480);

    [Theory]
    [InlineData(1280, 720, 160, 0, 960, 720)]
    [InlineData(1920, 1080, 240, 0, 1440, 1080)]
    [InlineData(5120, 1440, 1600, 0, 1920, 1440)]
    [InlineData(640, 480, 0, 0, 640, 480)]
    [InlineData(800, 600, 0, 0, 800, 600)]
    public void Fit_pillarboxes_a_4_3_picture(int w, int h, int x, int y, int pw, int ph) =>
        Assert.Equal(new PixelRect(x, y, pw, ph), Fit.Picture(new PixelRect(0, 0, w, h)));

    [Fact]
    public void Integer_uses_the_largest_whole_multiple_centred() =>
        Assert.Equal(new PixelRect(320, 60, 1280, 960),
            new GameDisplaySettings(PictureScaling.WholeMultiple, 640, 480).Picture(new PixelRect(0, 0, 1920, 1080)));

    [Fact]
    public void Integer_in_a_window_smaller_than_the_game_falls_back_to_fit() =>
        Assert.Equal(new PixelRect(0, 30, 400, 300),
            new GameDisplaySettings(PictureScaling.WholeMultiple, 640, 480).Picture(new PixelRect(0, 0, 400, 360)));

    [Fact]
    public void Stretch_fills_the_client() =>
        Assert.Equal(new PixelRect(1600, 167, 1920, 1080),
            new GameDisplaySettings(PictureScaling.Stretch, 640, 480).Picture(new PixelRect(1600, 167, 1920, 1080)));

    [Fact]
    public void The_picture_follows_the_client_position() =>
        Assert.Equal(new PixelRect(5600, -65, 960, 720), Fit.Picture(new PixelRect(5440, -65, 1280, 720)));

    [Fact]
    public void A_window_with_a_title_bar_is_windowed() =>
        Assert.Equal(GameWindowMode.Windowed,
            GameWindowInfo.Classify(hasCaption: true, topmost: false, new PixelRect(1600, 167, 1920, 1080), new PixelRect(0, 0, 5120, 1440)));

    [Fact]
    public void A_borderless_window_smaller_than_the_monitor_is_windowed() =>
        Assert.Equal(GameWindowMode.Windowed,
            GameWindowInfo.Classify(hasCaption: false, topmost: false, new PixelRect(1280, 0, 2560, 1440), new PixelRect(0, 0, 5120, 1440)));

    [Fact]
    public void A_window_covering_the_monitor_is_borderless() =>
        Assert.Equal(GameWindowMode.Borderless,
            GameWindowInfo.Classify(hasCaption: false, topmost: false, new PixelRect(0, 0, 5120, 1440), new PixelRect(0, 0, 5120, 1440)));

    [Fact]
    public void A_topmost_window_covering_the_monitor_is_exclusive_fullscreen() =>
        Assert.Equal(GameWindowMode.ExclusiveFullscreen,
            GameWindowInfo.Classify(hasCaption: false, topmost: true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080)));
}
