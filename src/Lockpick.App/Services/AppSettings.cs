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

    /// <summary>The game's display settings file as it was before Lockpick first changed it (kept outside the game folder).</summary>
    public static string DisplayBackup => Path.Combine(DataRoot, "backup", "game-display");

    /// <summary>Every run attempt, a JSON-lines file per split set.</summary>
    public static string Runs => Path.Combine(DataRoot, "runs");

    /// <summary>Secrets (the speedrun.com API key), each encrypted for the Windows user (DPAPI); never in settings.</summary>
    public static string Secrets => Path.Combine(DataRoot, "secrets");
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

    /// <summary>Controller buttons for the same actions (TrainerHotkeys.Actions ids → GamepadBinding values).</summary>
    public IReadOnlyDictionary<string, string> GamepadActions { get; init; } = new Dictionary<string, string>();

    /// <summary>Lockpick's API for other tools (docs/API.md): off unless the player turns it on.</summary>
    public bool ApiEnabled { get; init; }

    /// <summary>Listen for other devices on the network too (not just this PC).</summary>
    public bool ApiAllowNetwork { get; init; }

    public int ApiPort { get; init; } = Lockpick.Api.ApiOptions.DefaultPort;

    /// <summary>The API token; made when the API is first turned on, replaced on request.</summary>
    public string? ApiToken { get; init; }

    /// <summary>Let API clients use trainer commands (still only while Trainer Mode is on).</summary>
    public bool ApiAllowTrainer { get; init; }

    /// <summary>Show the overlay over the game window (click-through; never takes focus).</summary>
    public bool OverlayEnabled { get; init; }

    /// <summary>Where the time / HP panel sat before the overlay had several widgets (read as that widget's corner).</summary>
    public string OverlayCorner { get; init; } = OverlayCorners.TopRight;

    /// <summary>Each overlay widget's corner, or "Off" (<see cref="OverlayLayout"/>); missing: the widget's default.</summary>
    public IReadOnlyDictionary<string, string> OverlayWidgetCorners { get; init; } = new Dictionary<string, string>();

    /// <summary>How the overlay's map looks and which views its hotkey cycles through.</summary>
    public MapOverlaySettings MapOverlay { get; init; } = new();

    /// <summary>The split set runs are timed against (Runs page), or null: runs off.</summary>
    public string? RunSetId { get; init; }

    /// <summary>LiveSplit files the player imported (read again at start).</summary>
    public IReadOnlyList<string> ImportedSplitFiles { get; init; } = [];

    /// <summary>A loaded save starts an attempt too (practice from a save), not only a new game.</summary>
    public bool RunStartOnLoad { get; init; }

    /// <summary>How Lockpick works with LiveSplit (<see cref="LiveSplitModes"/>): off, follow or drive.</summary>
    public string LiveSplitMode { get; init; } = LiveSplitModes.Off;

    /// <summary>LiveSplit's TCP server port (its default).</summary>
    public int LiveSplitPort { get; init; } = 16834;

    /// <summary>Show speedrun.com's leaderboard on the Runs page (Lockpick goes online only when this is on).</summary>
    public bool SpeedrunComLeaderboard { get; init; }

    /// <summary>The player's speedrun.com name and id (from their API key or their name); never the key itself.</summary>
    public string? SpeedrunComUser { get; init; }

    public string? SpeedrunComUserId { get; init; }

    /// <summary>The leaderboard chosen per split set (set id → "filter=choice;…"), when not the one its name suggests.</summary>
    public IReadOnlyDictionary<string, string> SpeedrunComBoards { get; init; } = new Dictionary<string, string>();

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

/// <summary>Where the overlay sits on the game's picture. Not top-left by default: Discord's overlay toast shows there.</summary>
public static class OverlayCorners
{
    public const string TopRight = "Top right";
    public const string BottomRight = "Bottom right";
    public const string BottomLeft = "Bottom left";
    public const string TopLeft = "Top left";

    /// <summary>Centred at the top of the picture (big messages).</summary>
    public const string TopCentre = "Top centre";

    public static IReadOnlyList<string> All { get; } = [TopRight, BottomRight, BottomLeft, TopLeft, TopCentre];
}

/// <summary>The overlay map's views: what the map hotkey cycles through, in this order.</summary>
public static class MapViews
{
    /// <summary>The minimap in its corner: the area around the player.</summary>
    public const string Around = "around";

    /// <summary>The whole room over the game's picture, on a dark backdrop.</summary>
    public const string Room = "room";

    /// <summary>The whole room over the game's picture, see-through.</summary>
    public const string RoomSeeThrough = "room-see-through";

    /// <summary>Every room of the floor in place, over the game's picture (the map's zoom key moves in on the player).</summary>
    public const string Floor = "floor";

    /// <summary>The same, see-through.</summary>
    public const string FloorSeeThrough = "floor-see-through";

    /// <summary>No map until the key is pressed again.</summary>
    public const string Hidden = "hidden";

    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        (Around, "Minimap (around you)"),
        (Room, "Whole room, full screen"),
        (RoomSeeThrough, "Whole room, full screen, see-through"),
        (Floor, "Whole floor, full screen"),
        (FloorSeeThrough, "Whole floor, full screen, see-through"),
        (Hidden, "Hidden"),
    ];

    /// <summary>Covers the game's picture instead of sitting in a corner.</summary>
    public static bool IsFull(string view) => view is Room or RoomSeeThrough or Floor or FloorSeeThrough;

    /// <summary>Shows the whole floor rather than one room.</summary>
    public static bool IsFloor(string view) => view is Floor or FloorSeeThrough;

    /// <summary>Drawn see-through over the game.</summary>
    public static bool IsSeeThrough(string view) => view is RoomSeeThrough or FloorSeeThrough;
}

/// <summary>The overlay map's options (Settings).</summary>
public sealed record MapOverlaySettings
{
    /// <summary>The views the map hotkey cycles through (<see cref="MapViews"/>); the first is shown at start.</summary>
    public IReadOnlyList<string> Views { get; init; } = [MapViews.Around, MapViews.Room, MapViews.RoomSeeThrough, MapViews.Floor];

    /// <summary>World units from the player to the minimap's nearer edge.</summary>
    public int Reach { get; init; } = 6000;

    /// <summary>The minimap's side, in the overlay's own units (designed for a 480-pixel-high picture).</summary>
    public int Size { get; init; } = 130;

    /// <summary>How strongly the see-through map is drawn over the game, in percent.</summary>
    public int SeeThroughOpacity { get; init; } = 70;

    /// <summary>Names of doors, items and people on the full-screen map.</summary>
    public bool Names { get; init; } = true;

    public static IReadOnlyList<(int Value, string Name)> ReachChoices { get; } = [(4000, "Close"), (6000, "Medium"), (9000, "Far")];

    public static IReadOnlyList<(int Value, string Name)> SizeChoices { get; } = [(100, "Small"), (130, "Medium"), (170, "Large")];
}

/// <summary>The overlay's widgets and where each sits. Widgets sharing a corner stack in this order from the corner.</summary>
public static class OverlayLayout
{
    public const string Off = "Off";
    public const string Status = "status";
    public const string Minimap = "minimap";
    public const string Enemies = "enemies";
    public const string Splits = "splits";
    public const string Feed = "feed";
    public const string Banner = "banner";
    public const string LiveSplit = "livesplit";

    public static IReadOnlyList<(string Id, string Name, string Default)> Widgets { get; } =
    [
        (Status, "Time, HP, room, challenge", OverlayCorners.TopRight),
        (Minimap, "Minimap", OverlayCorners.BottomRight),
        (Enemies, "Enemies in the room (HP)", OverlayCorners.BottomLeft),
        (Splits, "Run splits", OverlayCorners.TopRight),
        (Feed, "What just happened", OverlayCorners.BottomLeft),
        (Banner, "Big messages (challenge failed, died, run finished)", OverlayCorners.TopCentre),
        (LiveSplit, "LiveSplit timer (LiveSplit's look, by livesplit-core)", Off),
    ];

    /// <summary>A corner, or off.</summary>
    public static IReadOnlyList<string> Choices { get; } = [.. OverlayCorners.All, Off];

    public static string Corner(AppSettings settings, string id) =>
        settings.OverlayWidgetCorners.TryGetValue(id, out var corner) && Choices.Contains(corner) ? corner
        : id == Status && OverlayCorners.All.Contains(settings.OverlayCorner) ? settings.OverlayCorner
        : Widgets.FirstOrDefault(w => w.Id == id).Default ?? Off;
}
