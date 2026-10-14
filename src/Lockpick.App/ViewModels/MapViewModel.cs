using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Map page: the room the player is in, drawn from the player's own room files (walls, furniture, doors, items for
/// this character), with the player's position and facing live. Read-only.
/// </summary>
public sealed partial class MapViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

    private readonly AppServices _services;
    private IRoomMapSource? _maps;
    private bool _mapsTried;
    private bool _visible;
    private DateTimeOffset _lastUpdate;

    public MapViewModel(AppServices services, LiveState state)
        : base("Map", "Icon.Map")
    {
        _services = services;
        State = state;
        state.PropertyChanged += OnStateChanged;
    }

    public LiveState State { get; }

    [ObservableProperty]
    public partial RoomMap? Map { get; private set; }

    [ObservableProperty]
    public partial double PlayerX { get; private set; }

    [ObservableProperty]
    public partial double PlayerZ { get; private set; }

    [ObservableProperty]
    public partial double PlayerAngle { get; private set; }

    [ObservableProperty]
    public partial bool HasPlayer { get; private set; }

    /// <summary>Items the game has placed in this room right now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlacedItem>? PlacedItems { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Start or load a game to see the room you're in.";

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    public override void OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Refresh(force: true);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_visible && e.PropertyName == nameof(LiveState.Snapshot))
        {
            Refresh(force: false);
        }
    }

    private IRoomMapSource? Maps()
    {
        if (!_mapsTried)
        {
            _mapsTried = true;
            var exe = _services.ResolveInstalledExecutable();
            var directory = exe is null ? null : Path.GetDirectoryName(exe);
            _maps = directory is null ? null : _services.Adapter.CreateRoomMaps(directory);
        }

        return _maps;
    }

    private void Refresh(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastUpdate < Throttle)
        {
            return;
        }

        _lastUpdate = now;
        if (State.Snapshot is not { } s || !s.Location.TryGet(out var room))
        {
            HasPlayer = false;
            return;
        }

        var maps = Maps();
        if (maps is null)
        {
            Map = null;
            Heading = "The game's room files weren't found — set the game path in Settings.";
            Details = string.Empty;
            return;
        }

        var character = s.Character.TryGet(out var c) ? c : PlayableCharacter.Unknown;
        var map = maps.Load(room, character);
        if (!ReferenceEquals(map, Map))
        {
            Map = map;
            Details = map is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture,
                    $"{map.Doors.Count} doors · {map.Items.Count} item spots · {map.Cameras.Count} cameras · from your install's room files");
        }

        Heading = map is null ? $"Room {room.Id} — no map for this room" : $"Room {room.Id}";
        var placed = s.PlacedItems.TryGet(out var items) ? items : null;
        if (placed is null != PlacedItems is null || (placed is not null && PlacedItems is not null && !placed.SequenceEqual(PlacedItems)))
        {
            PlacedItems = placed;
        }
        if (s.Position.TryGet(out var p))
        {
            PlayerX = p.X;
            PlayerZ = p.Z;
            PlayerAngle = s.RawFields.TryGetValue("yaw", out var yaw) && yaw.HasValue ? (short)yaw.Value : 0;
            HasPlayer = true;
        }
        else
        {
            HasPlayer = false;
        }
    }
}
