using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>
/// The map in its own window (pop-out): a live map with its own view, independent of the overlay's map key — around
/// the player (how far it reaches), the whole room, or the whole floor (the floor you're on, e.g. the mansion's
/// first floor; closer zooms follow you) — with or without names. Its choices are kept with the window.
/// </summary>
public sealed partial class MapPopOutViewModel : ObservableObject
{
    /// <summary>The floor view's zooms: the whole floor, then closer on the player.</summary>
    public static readonly IReadOnlyList<(double Zoom, string Name)> FloorZooms = [(1, "Whole floor"), (2.5, "Closer"), (5, "Closest")];

    public static readonly IReadOnlyList<(string Id, string Name)> Views =
    [
        (PopOutMapViews.Around, "Around you"),
        (PopOutMapViews.Room, "Whole room"),
        (PopOutMapViews.Floor, "Whole floor"),
    ];

    public MapPopOutViewModel(MapViewModel map, PopOutOptions options)
    {
        Map = map;
        View = Views.Any(v => v.Id == options.MapView) ? options.MapView : PopOutMapViews.Around;
        Reach = Math.Clamp(options.MapReach, 1000, 30000);
        ZoomLevel = Math.Clamp(options.MapZoom, 0, FloorZooms.Count - 1);
        Names = options.MapNames;
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.Map) or nameof(MapViewModel.Floor) or nameof(MapViewModel.HasPlayer) or nameof(MapViewModel.InGame))
            {
                OnPropertyChanged(nameof(HasMap));
            }
        };
    }

    public MapViewModel Map { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFloor), nameof(ViewRadius), nameof(HasMap), nameof(RoomNames))]
    public partial string View { get; set; }

    /// <summary>World units from the player to the nearer edge, for the view around you.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewRadius))]
    public partial int Reach { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FloorZoom), nameof(Follow))]
    public partial int ZoomLevel { get; set; }

    /// <summary>Names of doors, items and people (on the whole room and the floor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNames))]
    public partial bool Names { get; set; }

    public bool IsFloor => View == PopOutMapViews.Floor;

    /// <summary>0 for the whole room, else the reach around the player.</summary>
    public double ViewRadius => View == PopOutMapViews.Around ? Reach : 0;

    /// <summary>The view around you is too small for names.</summary>
    public bool RoomNames => Names && View == PopOutMapViews.Room;

    public double FloorZoom => FloorZooms[Math.Clamp(ZoomLevel, 0, FloorZooms.Count - 1)].Zoom;

    public bool Follow => FloorZoom > 1;

    /// <summary>Something to draw: in a game (not after a death or at the title) with a room or floor known.</summary>
    public bool HasMap => Map.InGame && (IsFloor ? Map.Floor is not null : Map.Map is not null && Map.HasPlayer);
}
