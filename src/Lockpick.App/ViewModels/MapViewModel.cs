using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Controls;
using Lockpick.App.Services;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Map page: the room the player is in, drawn from the player's own room files (walls, furniture, doors, items for
/// this character), with the player's position and facing live. Read-only.
/// </summary>
/// <summary>One legend row: a label and the marker drawn for it.</summary>
public sealed record LegendEntry(string Label, Geometry Icon, IBrush Fill);

public sealed partial class MapViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

    private readonly AppServices _services;
    private IRoomMapSource? _maps;
    private bool _mapsTried;
    private bool _visible;
    private bool _keepUpdating;
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

    /// <summary>Item boxes and typewriters the game has active now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlacedFixture>? LiveFixtures { get; private set; }

    /// <summary>Enemies and NPCs in the room right now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LiveActor>? Actors { get; private set; }

    /// <summary>Door label per target room ("106 Main hall"), falling back to the room id.</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, string>? DoorNames { get; private set; }

    /// <summary>The legend's people and enemy rows: one per family present in the room now.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LegendEntry> LegendActors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool HasPlacedItems { get; private set; }

    /// <summary>The legend's Items heading shows only when a row under it does.</summary>
    public bool HasItemRows => HasPlacedItems || HasFilledSockets || ShowAbsentItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool HasFilledSockets { get; private set; }

    [ObservableProperty]
    public partial bool HasItemBox { get; private set; }

    [ObservableProperty]
    public partial bool HasTypewriter { get; private set; }

    /// <summary>Whether the map also draws the room's items you've taken or that come later (off: only what's there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool ShowAbsentItems { get; set; }

    /// <summary>Room-file items already taken in this game.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<MapItem>? TakenItems { get; private set; }

    /// <summary>Use spots whose item has been put in place in this game.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<MapSocket>? FilledSockets { get; private set; }

    /// <summary>Items the game has placed in this room right now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlacedItem>? PlacedItems { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Start or load a game to see the room you're in.";

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    /// <summary>Keep the map up to date while the page is hidden too (the overlay's minimap shows it).</summary>
    public bool KeepUpdating
    {
        get => _keepUpdating;
        set
        {
            if (value && !_keepUpdating && !_visible)
            {
                _keepUpdating = true;
                Refresh(force: true);
            }

            _keepUpdating = value;
        }
    }

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
        if ((_visible || KeepUpdating) && e.PropertyName == nameof(LiveState.Snapshot))
        {
            Refresh(force: false);
        }
    }

    private void UpdateLegend(IReadOnlyList<LiveActor> actors)
    {
        var rows = actors.GroupBy(a => ActorStyles.For(a).Family)
            .Select(g =>
            {
                var (shape, color, _, family) = ActorStyles.For(g.First());
                // People and bosses are named (e.g. "People: Barry", "Boss: Yawn").
                var names = string.Join(", ", g.Select(a => a.Name).Distinct());
                var label = family is "People" or "Boss" ? $"{family}: {names}" : family;
                return new LegendEntry(label, ActorStyles.Icon(shape), new SolidColorBrush(color));
            })
            .ToList();
        if (rows.Select(r => r.Label).SequenceEqual(LegendActors.Select(r => r.Label)))
        {
            return;
        }

        LegendActors.Clear();
        foreach (var row in rows)
        {
            LegendActors.Add(row);
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
            DoorNames = map is null ? null : map.Doors.Select(d => d.Target).Distinct()
                .ToDictionary(t => t, t => _services.Adapter.RoomName(t) is { } n ? $"{t} {n}" : t);
            Details = map is null
                ? string.Empty
                : $"{Count(map.Doors.Count, "door")} · {Count(map.Items.Count, "item spot")} · {Count(map.Cameras.Count, "camera")} · from your install's room files";
        }

        var name = _services.Adapter.RoomName(room.Id);
        var title = name is null ? $"Room {room.Id}" : $"Room {room.Id} · {name}";
        Heading = map is null ? $"{title} — no map for this room" : title;
        if (map is not null)
        {
            var taken = map.Items.Where(i => maps.IsTaken(i, s) == true).ToHashSet();
            if (TakenItems is null || !taken.SetEquals(TakenItems))
            {
                TakenItems = taken;
            }

            var filled = map.Sockets.Where(k => maps.IsFilled(map, k, s) == true).ToHashSet();
            if (FilledSockets is null || !filled.SetEquals(FilledSockets))
            {
                FilledSockets = filled;
            }
        }

        var fixtures = s.PlacedFixtures.TryGet(out var active) ? active : null;
        if (fixtures is null != LiveFixtures is null || (fixtures is not null && LiveFixtures is not null && !fixtures.SequenceEqual(LiveFixtures)))
        {
            LiveFixtures = fixtures;
        }

        var actors = s.Actors.TryGet(out var live) ? live : null;
        if (actors is null != Actors is null || (actors is not null && Actors is not null && !actors.SequenceEqual(Actors)))
        {
            Actors = actors;
            UpdateLegend(actors ?? []);
        }

        HasFilledSockets = FilledSockets is { Count: > 0 };
        HasItemBox = LiveFixtures?.Any(f => f.Kind == FixtureKind.ItemBox) == true;
        HasTypewriter = LiveFixtures?.Any(f => f.Kind == FixtureKind.Typewriter) == true;

        var placed = s.PlacedItems.TryGet(out var items) ? items : null;
        HasPlacedItems = placed is { Count: > 0 } || (placed is null && map is { Items.Count: > 0 });
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

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");
}
