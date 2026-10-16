using Lockpick.Core.Display;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

public sealed class Re1DisplayConfigTests : IDisposable
{
    private const string Owner = "[dxcfg]\ndisplay=1920x1080@60\npresentation=windowed\naspect=enabled\nscaling=fit\nvsync=application\ngamma=1.0\n";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-dxcfg-" + Guid.NewGuid().ToString("N"));
    private readonly Re1DisplayConfig _config;

    public Re1DisplayConfigTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "dxcfg.ini"), Owner);
        _config = new Re1DisplayConfig(Path.Combine(_folder, "dxcfg.ini"), Path.Combine(_folder, "backup"));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Reads_the_owner_s_settings() =>
        Assert.Equal(new DisplayConfiguration(DisplayPresentation.Windowed, "1920x1080@60", PictureScaling.Fit), _config.Read());

    [Fact]
    public void Writes_only_its_own_keys_and_reads_them_back()
    {
        _config.Write(new DisplayConfiguration(DisplayPresentation.Fullscreen, DisplayConfiguration.Desktop, PictureScaling.WholeMultiple));
        var lines = File.ReadAllLines(_config.Path);
        Assert.Contains("presentation=fullscreen", lines);
        Assert.Contains("display=desktop", lines);
        Assert.Contains("scaling=integer", lines);
        Assert.Contains("vsync=application", lines);
        Assert.Contains("gamma=1.0", lines);
        Assert.Equal(7, lines.Length);
        Assert.Equal(new DisplayConfiguration(DisplayPresentation.Fullscreen, DisplayConfiguration.Desktop, PictureScaling.WholeMultiple), _config.Read());
    }

    [Fact]
    public void Stretch_turns_the_aspect_off()
    {
        _config.Write(new DisplayConfiguration(DisplayPresentation.Windowed, "1280x720@60", PictureScaling.Stretch));
        Assert.Contains("aspect=disabled", File.ReadAllLines(_config.Path));
        Assert.Equal(PictureScaling.Stretch, _config.Read().Scaling);
    }

    [Fact]
    public void The_original_is_backed_up_once_and_can_be_put_back()
    {
        Assert.False(_config.HasBackup);
        _config.Write(new DisplayConfiguration(DisplayPresentation.Fullscreen, DisplayConfiguration.Desktop, PictureScaling.Fit));
        _config.Write(new DisplayConfiguration(DisplayPresentation.Windowed, DisplayConfiguration.GameOwn, PictureScaling.Fit));
        Assert.True(_config.HasBackup);
        Assert.Equal(Owner.Replace("\n", Environment.NewLine), File.ReadAllText(Path.Combine(_folder, "backup", "dxcfg.ini")).Replace("\r\n", Environment.NewLine).Replace("\n", Environment.NewLine));
        _config.RestoreBackup();
        Assert.Equal(new DisplayConfiguration(DisplayPresentation.Windowed, "1920x1080@60", PictureScaling.Fit), _config.Read());
    }

    [Fact]
    public void Missing_keys_are_added_to_the_section()
    {
        File.WriteAllText(_config.Path, "[dxcfg]\nvsync=application\n");
        _config.Write(new DisplayConfiguration(DisplayPresentation.Windowed, DisplayConfiguration.Desktop, PictureScaling.Fit));
        var lines = File.ReadAllLines(_config.Path);
        Assert.Equal("[dxcfg]", lines[0]);
        Assert.Contains("display=desktop", lines);
        Assert.Contains("vsync=application", lines);
    }

    [Fact]
    public void Options_read_the_file_or_fall_back_to_their_defaults()
    {
        var values = _config.ReadOptions();
        Assert.Equal("application", values["vsync"]);
        Assert.Equal("1.0", values["gamma"]);
        Assert.Equal("application", values["anisotropic"]); // not in the file: the default
        Assert.Equal("application", values["antialiasing"]);
    }

    [Fact]
    public void Options_are_written_and_values_the_game_doesnt_know_are_refused()
    {
        _config.WriteOptions(new Dictionary<string, string> { ["gamma"] = "1.2", ["antialiasing"] = "msaa_4x", ["vsync"] = "sometimes", ["mystery"] = "1" });
        var lines = File.ReadAllLines(_config.Path);
        Assert.Contains("gamma=1.2", lines);
        Assert.Contains("antialiasing=msaa_4x", lines);
        Assert.Contains("vsync=application", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("mystery", StringComparison.Ordinal));
        Assert.Equal("1.2", _config.ReadOptions()["gamma"]);
        Assert.True(_config.HasBackup);
    }

    [Fact]
    public void Every_option_offers_its_default()
    {
        foreach (var option in _config.Options)
        {
            Assert.Contains(option.Choices, c => c.Value == option.Default);
        }
    }

    [Theory]
    [InlineData("1920x1080@60", 1920, 1080)]
    [InlineData("640x480@60", 640, 480)]
    [InlineData("2560x1440", 2560, 1440)]
    public void Mode_sizes_are_parsed(string resolution, int width, int height) =>
        Assert.Equal((width, height), DisplayConfiguration.Size(resolution));

    [Fact]
    public void Desktop_and_the_game_s_own_have_no_size()
    {
        Assert.Null(DisplayConfiguration.Size(DisplayConfiguration.Desktop));
        Assert.Null(DisplayConfiguration.Size(DisplayConfiguration.GameOwn));
    }
}
