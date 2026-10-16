using Lockpick.Core.Display;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Reads how GOG's DirectDraw wrapper scales the game's 640×480 picture (<c>dxcfg.ini</c>; docs/DISPLAY.md).</summary>
public static class Re1Display
{
    public const int Width = 640;
    public const int Height = 480;

    /// <summary>The wrapper's defaults (aspect kept, screen fit), also used when the file is missing or unreadable.</summary>
    public static GameDisplaySettings Default { get; } = new(PictureScaling.Fit, Width, Height);

    public static GameDisplaySettings Read(string iniPath)
    {
        try
        {
            return File.Exists(iniPath) ? Parse(File.ReadAllLines(iniPath)) : Default;
        }
        catch (IOException)
        {
            return Default;
        }
        catch (UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public static GameDisplaySettings Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && !line.TrimStart().StartsWith(';'))
            {
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        var scaling = values.GetValueOrDefault("aspect") is { } aspect && aspect.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            ? PictureScaling.Stretch
            : values.GetValueOrDefault("scaling") is { } s && s.Equals("integer", StringComparison.OrdinalIgnoreCase)
                ? PictureScaling.WholeMultiple
                : PictureScaling.Fit;
        return new GameDisplaySettings(scaling, Width, Height);
    }
}
