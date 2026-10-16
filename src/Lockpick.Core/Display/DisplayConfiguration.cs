namespace Lockpick.Core.Display;

/// <summary>Windowed (a borderless window when it fills the screen) or exclusive fullscreen.</summary>
public enum DisplayPresentation
{
    Windowed,
    Fullscreen,
}

/// <summary>
/// The game's own display settings as the player can change them from Lockpick (docs/DISPLAY.md).
/// <paramref name="Resolution"/> is <see cref="Desktop"/>, <see cref="GameOwn"/> or "WIDTHxHEIGHT@HZ".
/// </summary>
public sealed record DisplayConfiguration(DisplayPresentation Presentation, string Resolution, PictureScaling Scaling)
{
    /// <summary>The desktop's own resolution (windowed: a borderless window covering the monitor).</summary>
    public const string Desktop = "desktop";

    /// <summary>The game's own resolution (RE1: 640×480).</summary>
    public const string GameOwn = "application";

    /// <summary>A "WIDTHxHEIGHT@HZ" resolution.</summary>
    public static string Mode(int width, int height, int hz) => $"{width}x{height}@{hz}";

    /// <summary>Width and height of a "WIDTHxHEIGHT@HZ" resolution, or null for desktop / game's own.</summary>
    public static (int Width, int Height)? Size(string resolution)
    {
        var at = resolution.IndexOf('@', StringComparison.Ordinal);
        var parts = (at < 0 ? resolution : resolution[..at]).Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : null;
    }
}

/// <summary>One choice of a game-specific display option: the value written to the game's settings and its label.</summary>
public sealed record DisplayOptionChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A game-specific display option (e.g. vsync, gamma, antialiasing) with the values the game accepts.</summary>
public sealed record DisplayOption(string Key, string Label, IReadOnlyList<DisplayOptionChoice> Choices, string Default);

/// <summary>Reads and writes the game's display settings in the player's install (only on the player's click).</summary>
public interface IGameDisplayConfig
{
    /// <summary>The game's other display options, beyond windowed / fullscreen, resolution and scaling.</summary>
    IReadOnlyList<DisplayOption> Options { get; }

    /// <summary>The current value of each of <see cref="Options"/> (its default when the file doesn't set it).</summary>
    IReadOnlyDictionary<string, string> ReadOptions();

    /// <summary>Writes option values (keys from <see cref="Options"/>, values from their choices), keeping every other setting.</summary>
    void WriteOptions(IReadOnlyDictionary<string, string> values);

    /// <summary>The settings file (RE1: GOG's <c>dxcfg.ini</c>).</summary>
    string Path { get; }

    /// <summary>True once Lockpick has kept a copy of the file as it was before its first change.</summary>
    bool HasBackup { get; }

    DisplayConfiguration Read();

    /// <summary>Writes <paramref name="configuration"/>, keeping every other setting; backs the file up first, once.</summary>
    void Write(DisplayConfiguration configuration);

    /// <summary>Puts the file back as it was before Lockpick first changed it.</summary>
    void RestoreBackup();
}
