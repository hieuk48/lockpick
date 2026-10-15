using System.Text.Json;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>Per-user locations. Everything Lockpick writes lives under %LOCALAPPDATA%\Lockpick.</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lockpick");

    public static string Logs => Path.Combine(DataRoot, "logs");

    public static string Recordings => Path.Combine(DataRoot, "recordings");

    public static string Profiles => Path.Combine(DataRoot, "profiles");

    public static string Captures => Path.Combine(DataRoot, "captures");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>Every trainer action (JSON lines): what was written, from what to what, and the result.</summary>
    public static string TrainerLog => Path.Combine(DataRoot, "trainer", "actions.jsonl");
}

public sealed record AppSettings
{
    /// <summary>Explicit path to the game executable. When null, the GOG install is located via the registry.</summary>
    public string? GameExecutablePath { get; init; }

    /// <summary>Directory with additional/override version profiles (*.json).</summary>
    public string? ProfileDirectory { get; init; }

    /// <summary>Version of the first-time setup the user has completed (0 = never). See SetupViewModel.CurrentVersion.</summary>
    public int SetupVersion { get; init; }

    /// <summary>Check GitHub releases for updates in the background (installed builds only).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Also offer pre-release versions.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>Hotkey that adds a capture marker while the game has focus (a GlobalHotkey.Choices name, or "Off").</summary>
    public string MarkerHotkey { get; init; } = "Ctrl+Shift+M";

    /// <summary>
    /// Controller button that adds a capture marker (GamepadBinding: "L2" = LT / L2, "Off", or a bound button such as
    /// "hid:0810:0001/5"). L2 does nothing in Resident Evil with the PlayStation layout, so a player can press it mid-run.
    /// </summary>
    public string MarkerButton { get; init; } = GamepadBinding.L2;

    /// <summary>
    /// Trainer hotkeys by action (TrainerHotkeys.Actions ids) to a GlobalHotkey.Choices name. Off unless the player sets
    /// one; they act only while Trainer Mode has write access.
    /// </summary>
    public IReadOnlyDictionary<string, string> TrainerHotkeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Set only while ask mode has the game frozen; lets the next start unfreeze it after a crash.</summary>
    public PausedGame? PausedGame { get; init; }
}

public sealed class SettingsStore(string path, ILogger logger)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read settings from {Path}; using defaults", path);
            Current = new AppSettings();
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
        Current = settings;
        logger.LogInformation("Settings saved to {Path}", path);
    }
}
