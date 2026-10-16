using Lockpick.Core.Display;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

public sealed class Re1DisplayTests
{
    [Fact]
    public void The_owner_s_settings_fit_the_picture() =>
        Assert.Equal(PictureScaling.Fit, Re1Display.Parse(
            ["[dxcfg]", "display=1920x1080@60", "presentation=windowed", "aspect=enabled", "scaling=fit"]).Scaling);

    [Fact]
    public void Integer_scaling_is_read() =>
        Assert.Equal(PictureScaling.WholeMultiple, Re1Display.Parse(["[dxcfg]", "aspect=enabled", "scaling=integer"]).Scaling);

    [Fact]
    public void Aspect_disabled_stretches_whatever_the_scaling() =>
        Assert.Equal(PictureScaling.Stretch, Re1Display.Parse(["[dxcfg]", "aspect=disabled", "scaling=integer"]).Scaling);

    [Fact]
    public void A_missing_file_gives_the_wrapper_defaults() =>
        Assert.Equal(Re1Display.Default, Re1Display.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "dxcfg.ini")));

    [Fact]
    public void The_game_picture_is_640_by_480()
    {
        var settings = Re1Display.Parse([]);
        Assert.Equal((640, 480), (settings.NativeWidth, settings.NativeHeight));
    }
}
