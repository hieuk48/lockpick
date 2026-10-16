using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Controls;
using Lockpick.App.Services;
using Lockpick.Core.Challenges;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// What the in-game overlay shows: the in-game time, HP, the room and the challenge's state (ROADMAP 0.3.0 sprint 2,
/// #54/#55). Widgets come and go with their data; nothing here can change the game.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    /// <summary>Gap between a widget and the picture's edge, in the overlay's own units.</summary>
    public const double Margin = 6;

    private static readonly IBrush PoisonBrush = new SolidColorBrush(Color.Parse("#B07BE0"));
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#E0060807"));
    private static readonly IBrush Floor = new SolidColorBrush(Color.Parse("#E0202B27"));
    private static readonly IBrush DimWalls = new SolidColorBrush(Color.Parse("#5E6B66"));
    private static readonly IBrush BrightWalls = new SolidColorBrush(Color.Parse("#C8D2CD"));

    private readonly AppServices _services;
    private MapOverlaySettings _mapSettings;
    private (double Width, double Height) _picture = (640, 480);
    private readonly Dictionary<(int Slot, int Type), int> _enemyMax = [];
    private string? _enemyRoom;

    public OverlayViewModel(AppServices services, LiveState state, MapViewModel map)
    {
        _services = services;
        _mapSettings = services.Settings.Current.MapOverlay;
        Map = map;
        MapView = Views(_mapSettings)[0];
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.Map) or nameof(MapViewModel.HasPlayer) or nameof(MapViewModel.Floor))
            {
                UpdateMinimap();
            }
        };
        services.TrainerHotkeys.Pressed += (_, action) =>
        {
            if (action == TrainerHotkeys.NextMapView)
            {
                Dispatcher.UIThread.Post(NextMapView);
            }
            else if (action == TrainerHotkeys.MapZoom)
            {
                Dispatcher.UIThread.Post(() => MinimapZoomLevel = (MinimapZoomLevel + 1) % FloorZooms.Length);
            }
        };
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LiveState.Snapshot))
            {
                Show(state.Snapshot);
            }
        };
        services.Challenges.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => Show(s));
        services.Runs.StatusChanged += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        services.RunLibrary.Changed += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        Show(state.Snapshot);
        Show(services.Challenges.Status);
    }

    /// <summary>The room map the minimap draws (the Map page's, kept up to date while the minimap is on).</summary>
    public MapViewModel Map { get; }

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    /// <summary>The map's current view (<see cref="MapViews"/>); the map hotkey moves to the next chosen one.</summary>
    [ObservableProperty]
    public partial string MapView { get; private set; }

    /// <summary>The map has something to show: a room map, the player on it, and a view that isn't hidden.</summary>
    [ObservableProperty]
    public partial bool HasMinimap { get; private set; }

    /// <summary>The map covers the game's picture instead of sitting in its corner.</summary>
    [ObservableProperty]
    public partial bool MinimapFull { get; private set; }

    /// <summary>0 for the whole room, else world units from the player to the minimap's nearer edge.</summary>
    [ObservableProperty]
    public partial double MinimapViewRadius { get; private set; }

    [ObservableProperty]
    public partial double MinimapWidth { get; private set; } = 130;

    [ObservableProperty]
    public partial double MinimapHeight { get; private set; } = 130;

    [ObservableProperty]
    public partial bool MinimapNames { get; private set; }

    /// <summary>The map shows the whole floor (a floor view) rather than a room.</summary>
    [ObservableProperty]
    public partial bool MinimapIsFloor { get; private set; }

    /// <summary>Which of <see cref="FloorZooms"/> the floor views use (the zoom key).</summary>
    [ObservableProperty]
    public partial int MinimapZoomLevel { get; set; }

    /// <summary>The floor view's zoom: 1 is the whole floor.</summary>
    [ObservableProperty]
    public partial double MinimapFloorZoom { get; private set; } = 1;

    /// <summary>Closer than the whole floor: the floor view keeps the player in the middle.</summary>
    [ObservableProperty]
    public partial bool MinimapFollow { get; private set; }

    private static readonly double[] FloorZooms = [1, 2.5, 5];

    [ObservableProperty]
    public partial double MinimapOpacity { get; private set; } = 1;

    /// <summary>Behind the map: dark, or nothing when see-through.</summary>
    [ObservableProperty]
    public partial IBrush MinimapBackdrop { get; private set; } = Backdrop;

    [ObservableProperty]
    public partial IBrush? MinimapFloor { get; private set; } = Floor;

    [ObservableProperty]
    public partial IBrush MinimapWalls { get; private set; } = DimWalls;

    [ObservableProperty]
    public partial IBrush MinimapBorder { get; private set; } = Brush("LpBorderBrush");

    [ObservableProperty]
    public partial string Health { get; private set; } = string.Empty;

    /// <summary>Fine, Caution, Danger or Poison, as the game's status screen shows it (empty when unknown).</summary>
    [ObservableProperty]
    public partial string Condition { get; private set; } = string.Empty;

    /// <summary>HP as a fraction of max HP, for the bar.</summary>
    [ObservableProperty]
    public partial double HealthFraction { get; private set; }

    /// <summary>Width of the HP bar's fill (the bar is 70 wide).</summary>
    [ObservableProperty]
    public partial double HealthBarWidth { get; private set; }

    /// <summary>Green, amber below 3/4, red below 1/4.</summary>
    [ObservableProperty]
    public partial IBrush HealthBrush { get; private set; } = Brush("LpGreenBrush");

    /// <summary>Green while the challenge is clean, red once it failed.</summary>
    [ObservableProperty]
    public partial IBrush ChallengeBrush { get; private set; } = Brush("LpGreenBrush");

    [ObservableProperty]
    public partial string Room { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasChallenge { get; private set; }

    [ObservableProperty]
    public partial bool ChallengeFailed { get; private set; }

    [ObservableProperty]
    public partial string Challenge { get; private set; } = string.Empty;

    /// <summary>Whether the overlay is showing, or why not (Settings).</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Off";

    /// <summary>Size of the widgets: they are designed for a 480-pixel-high picture and grow with the picture.</summary>
    [ObservableProperty]
    public partial double Scale { get; private set; } = 1;

    /// <summary>Takes the map options from Settings (cheap when unchanged; called by the overlay each tick).</summary>
    public void UseMapSettings(MapOverlaySettings settings)
    {
        if (ReferenceEquals(settings, _mapSettings))
        {
            return;
        }

        _mapSettings = settings;
        if (!Views(settings).Contains(MapView))
        {
            MapView = Views(settings)[0];
        }

        UpdateMinimap();
    }

    /// <summary>The map hotkey: the next of the chosen views.</summary>
    public void NextMapView()
    {
        var views = Views(_mapSettings);
        MapView = views[(views.IndexOf(MapView) + 1) % views.Count];
    }

    /// <summary>The chosen views, or just the minimap when none are.</summary>
    private static List<string> Views(MapOverlaySettings settings)
    {
        var views = settings.Views.Where(v => MapViews.All.Any(a => a.Id == v)).Distinct().ToList();
        return views.Count > 0 ? views : [MapViews.Around];
    }

    partial void OnMapViewChanged(string value) => UpdateMinimap();

    partial void OnMinimapZoomLevelChanged(int value) => UpdateMinimap();

    private void UpdateMinimap()
    {
        MinimapIsFloor = MapViews.IsFloor(MapView);
        HasMinimap = MapView != MapViews.Hidden && (MinimapIsFloor ? Map.Floor is not null : Map.Map is not null && Map.HasPlayer);
        MinimapFull = MapViews.IsFull(MapView);
        MinimapFloorZoom = FloorZooms[Math.Clamp(MinimapZoomLevel, 0, FloorZooms.Length - 1)];
        MinimapFollow = MinimapFloorZoom > 1;
        var seeThrough = MapViews.IsSeeThrough(MapView);
        MinimapViewRadius = MinimapFull ? 0 : Math.Clamp(_mapSettings.Reach, 1000, 30000);
        MinimapNames = MinimapFull && _mapSettings.Names;
        MinimapOpacity = seeThrough ? Math.Clamp(_mapSettings.SeeThroughOpacity, 10, 100) / 100.0 : 1;
        MinimapBackdrop = seeThrough ? Brushes.Transparent : Backdrop;
        MinimapBorder = seeThrough ? Brushes.Transparent : Brush("LpBorderBrush");
        MinimapFloor = seeThrough ? null : Floor;
        MinimapWalls = seeThrough ? BrightWalls : DimWalls;
        if (MinimapFull)
        {
            // The whole picture, less the margin and the frame (border and padding: 3 each side).
            MinimapWidth = Math.Max(100, Math.Floor((_picture.Width / Scale) - (2 * Margin) - 6));
            MinimapHeight = Math.Max(100, Math.Floor((_picture.Height / Scale) - (2 * Margin) - 6));
        }
        else
        {
            MinimapWidth = MinimapHeight = Math.Clamp(_mapSettings.Size, 60, 400);
        }
    }

    /// <summary>The game picture's size in the overlay's own units (pixels ÷ DPI scale): sets the scale and the size
    /// of the full-screen map.</summary>
    public void SetPicture(double widthDips, double heightDips)
    {
        Scale = Math.Clamp(heightDips / 480.0, 0.75, 3.0);
        if (_picture != (widthDips, heightDips))
        {
            _picture = (widthDips, heightDips);
            UpdateMinimap();
        }
    }

    private void Show(GameSnapshot? snapshot)
    {
        var inGame = snapshot?.InGameplay is { IsValid: true, Value: true };
        Time = inGame && snapshot!.GameTime is { IsValid: true } t ? Format(t.Value) : string.Empty;
        if (inGame && snapshot!.Health is { IsValid: true } h && h.Value.Maximum is > 0 and var max)
        {
            var fraction = Math.Clamp((double)h.Value.Current / max, 0, 1);
            Health = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, h.Value.Current)} / {max}");
            HealthFraction = fraction;
            HealthBarWidth = Math.Round(70 * fraction, 1);
            var condition = snapshot!.Condition is { IsValid: true } c ? c.Value : (HealthCondition?)null;
            Condition = condition?.ToString() ?? string.Empty;
            HealthBrush = condition switch
            {
                HealthCondition.Poison => PoisonBrush,
                HealthCondition.Danger => Brush("LpRedBrush"),
                HealthCondition.Caution => Brush("LpAmberBrush"),
                HealthCondition.Fine => Brush("LpGreenBrush"),
                _ => Brush(fraction < 0.25 ? "LpRedBrush" : fraction < 0.75 ? "LpAmberBrush" : "LpGreenBrush"),
            };
        }
        else
        {
            Health = string.Empty;
            Condition = string.Empty;
        }

        Room = inGame && snapshot!.Location is { IsValid: true } room
            ? _services.Adapter.RoomName(room.Value.Id) ?? room.Value.Id
            : string.Empty;
        ShowEnemies(inGame ? snapshot : null);
        ShowRun(snapshot);
    }

    /// <summary>The splits widget's rows: a window around the current split, with the last split always shown.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SplitRowData> SplitRows { get; private set; } = [];

    /// <summary>The run's in-game time ("12:34.5").</summary>
    [ObservableProperty]
    public partial string RunClock { get; private set; } = string.Empty;

    /// <summary>The splits' category.</summary>
    [ObservableProperty]
    public partial string RunTitle { get; private set; } = string.Empty;

    /// <summary>Personal best and best possible time.</summary>
    [ObservableProperty]
    public partial string RunComparison { get; private set; } = string.Empty;

    /// <summary>The clock is ahead of (or level with) the personal best at the current split; false when behind.</summary>
    [ObservableProperty]
    public partial bool RunBehind { get; private set; }

    private const int SplitsBefore = 2;
    private const int SplitsAfter = 3;

    private void ShowRun(GameSnapshot? snapshot)
    {
        var status = _services.Runs.Status;
        if (status.Set is not { } set || status.State == RunState.Off)
        {
            SplitRows = [];
            RunClock = RunTitle = RunComparison = string.Empty;
            return;
        }

        var comparison = _services.RunLibrary.Comparison(set);
        var now = status.GameTime(snapshot);
        var rows = RunRows.Build(status, comparison, now);
        var current = status.State == RunState.Running ? status.Current : status.State == RunState.Ready ? 0 : rows.Count - 1;
        var first = Math.Clamp(current - SplitsBefore, 0, Math.Max(0, rows.Count - 1 - SplitsBefore - SplitsAfter));
        var shown = rows.Skip(first).Take(SplitsBefore + SplitsAfter + 1).ToList();
        if (rows.Count > 0 && shown[^1] != rows[^1])
        {
            shown.Add(rows[^1]);
        }

        if (!shown.SequenceEqual(SplitRows))
        {
            SplitRows = shown;
        }

        RunTitle = set.Category;
        RunClock = now is { } t ? RunRows.Clock(t) : "0:00.0";
        RunBehind = rows.ElementAtOrDefault(current) is { IsCurrent: true, IsBehind: true };
        var pb = comparison.PersonalBest.Count > 0 ? comparison.PersonalBest[^1] : null;
        RunComparison = pb is { } p ? $"PB {RunRows.Clock(p)}" + (comparison.SumOfBest is { } sob ? $" · SoB {RunRows.Clock(sob)}" : string.Empty) : string.Empty;
    }

    /// <summary>The enemies and bosses in the room, with HP (the enemies widget). Empty when there are none.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EnemyRow> Enemies { get; private set; } = [];

    private void ShowEnemies(GameSnapshot? snapshot)
    {
        var room = snapshot?.Location is { IsValid: true } location ? location.Value.Id : null;
        if (room != _enemyRoom)
        {
            _enemyRoom = room;
            _enemyMax.Clear();
        }

        var rows = new List<EnemyRow>();
        foreach (var actor in snapshot?.Actors is { IsValid: true } actors ? actors.Value : [])
        {
            var (_, color, _, family) = ActorStyles.For(actor);
            // Plants, webs and the like have no HP worth showing (the mansion plant's is always 1).
            if (actor.Kind == ActorKind.Npc || (actor.Kind == ActorKind.Enemy && family.StartsWith("Plants", StringComparison.Ordinal)) || actor.Hp <= 0)
            {
                continue;
            }

            // Enemies have no max HP of their own: the bar is against the most this one has had in this room.
            var key = (actor.Slot, actor.Type);
            var max = Math.Max(actor.Hp, _enemyMax.GetValueOrDefault(key));
            _enemyMax[key] = max;
            rows.Add(new EnemyRow(actor.Name, actor.Hp, Math.Clamp((double)actor.Hp / max, 0, 1), color, actor.Kind == ActorKind.Boss));
        }

        rows.Sort((a, b) => b.IsBoss.CompareTo(a.IsBoss));
        if (!rows.SequenceEqual(Enemies))
        {
            Enemies = rows;
        }
    }

    private void Show(ChallengeStatus status)
    {
        HasChallenge = status.State is ChallengeState.Running or ChallengeState.Failed;
        ChallengeFailed = status.State == ChallengeState.Failed;
        ChallengeBrush = Brush(ChallengeFailed ? "LpRedBrush" : "LpGreenBrush");
        Challenge = status.State switch
        {
            ChallengeState.Running => $"{status.Challenge?.Name} · attempt {status.Attempt} · clean",
            ChallengeState.Failed => $"{status.Challenge?.Name} · failed: {status.FailReason}",
            _ => string.Empty,
        };
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : Brushes.White;

    private static string Format(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}");
}

/// <summary>One enemy in the enemies widget: its name, HP and HP against the most it has had in this room.</summary>
public sealed record EnemyRow(string Name, int Hp, double Fraction, Color Color, bool IsBoss);
