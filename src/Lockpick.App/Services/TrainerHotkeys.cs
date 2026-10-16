using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// System-wide hotkeys for trainer actions (registered hotkeys, like the marker key: Windows reports only these
/// combinations). A press is passed on as <see cref="Pressed"/>; the Trainer page ignores it unless Trainer Mode has
/// write access, so nothing happens while the game isn't attached or the mode is off.
/// </summary>
public sealed class TrainerHotkeys : IDisposable
{
    public const string FillHealth = "fill-health";
    public const string ToggleHealthLock = "toggle-health-lock";
    public const string ToggleInfiniteAmmo = "toggle-infinite-ammo";

    /// <summary>Shows or hides the overlay; works any time (it changes nothing in the game).</summary>
    public const string ToggleOverlay = "toggle-overlay";

    /// <summary>Shows the overlay map's next view (Settings: minimap, full-screen room, …); works any time.</summary>
    public const string NextMapView = "map-view";

    private readonly Dictionary<string, GlobalHotkey> _keys = [];
    private readonly ILogger _log;

    public TrainerHotkeys(ILogger log) => _log = log;

    /// <summary>Raised on a background thread with the action's id.</summary>
    public event EventHandler<string>? Pressed;

    /// <summary>The actions a hotkey can trigger, with the names shown in Settings.</summary>
    public static IReadOnlyList<(string Id, string Name)> Actions { get; } =
    [
        (FillHealth, "Fill HP"),
        (ToggleHealthLock, "Lock / unlock HP"),
        (ToggleInfiniteAmmo, "Infinite ammo on / off"),
        (ToggleOverlay, "Overlay on / off"),
        (NextMapView, "Map: next view"),
    ];

    /// <summary>The registered key per action (missing: off or refused by Windows).</summary>
    public IReadOnlyDictionary<string, string> Registered => _keys.ToDictionary(k => k.Key, k => k.Value.Name);

    /// <summary>
    /// Registers <paramref name="keyName"/> for <paramref name="action"/> ("Off" removes it). Returns null on success, or
    /// why it couldn't be used (taken by another action or the marker key, or by another program).
    /// </summary>
    public string? Set(string action, string keyName, string? markerHotkey)
    {
        if (_keys.Remove(action, out var old))
        {
            old.Dispose();
        }

        if (keyName == "Off" || !GlobalHotkey.Choices.ContainsKey(keyName))
        {
            return null;
        }

        if (keyName == markerHotkey || _keys.Values.Any(k => k.Name == keyName))
        {
            return $"{keyName} is already used by another Lockpick action.";
        }

        var hotkey = GlobalHotkey.Create(keyName)!;
        if (!hotkey.IsRegistered)
        {
            _log.LogWarning("Trainer hotkey {Key} is unavailable (error {Error})", keyName, hotkey.Error);
            hotkey.Dispose();
            return $"{keyName} is used by another program; choose another key.";
        }

        hotkey.Pressed += (_, _) => Pressed?.Invoke(this, action);
        _keys[action] = hotkey;
        return null;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }

        _keys.Clear();
    }
}
