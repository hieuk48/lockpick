using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>One map view in Settings: whether the map key stops on it.</summary>
public sealed partial class MapViewChoice(string id, string name, bool chosen) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool Chosen { get; set; } = chosen;
}

/// <summary>Settings for the overlay's map: the views its key cycles through, the minimap's reach and size, and how
/// the full-screen map looks.</summary>
public sealed partial class MapOverlayOptionsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly bool _loading = true;

    public MapOverlayOptionsViewModel(AppServices services)
    {
        _services = services;
        var current = services.Settings.Current.MapOverlay;
        Views = [.. MapViews.All.Select(v => new MapViewChoice(v.Id, v.Name, current.Views.Contains(v.Id)))];
        foreach (var view in Views)
        {
            view.PropertyChanged += (_, _) => Save(s => s with { Views = [.. Views.Where(v => v.Chosen).Select(v => v.Id)] });
        }

        Reach = Name(MapOverlaySettings.ReachChoices, current.Reach);
        Size = Name(MapOverlaySettings.SizeChoices, current.Size);
        SeeThroughOpacity = current.SeeThroughOpacity;
        Names = current.Names;
        _loading = false;
    }

    /// <summary>The views, in the order the map key shows them.</summary>
    public IReadOnlyList<MapViewChoice> Views { get; }

    public IReadOnlyList<string> ReachChoices { get; } = [.. MapOverlaySettings.ReachChoices.Select(c => c.Name)];

    public IReadOnlyList<string> SizeChoices { get; } = [.. MapOverlaySettings.SizeChoices.Select(c => c.Name)];

    /// <summary>How far around the player the minimap reaches.</summary>
    [ObservableProperty]
    public partial string Reach { get; set; }

    /// <summary>The minimap's size.</summary>
    [ObservableProperty]
    public partial string Size { get; set; }

    /// <summary>How strongly the see-through map is drawn, in percent.</summary>
    [ObservableProperty]
    public partial double SeeThroughOpacity { get; set; }

    /// <summary>Names of doors, items and people on the full-screen map.</summary>
    [ObservableProperty]
    public partial bool Names { get; set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    partial void OnReachChanged(string value) =>
        Save(s => s with { Reach = Value(MapOverlaySettings.ReachChoices, value, s.Reach) });

    partial void OnSizeChanged(string value) =>
        Save(s => s with { Size = Value(MapOverlaySettings.SizeChoices, value, s.Size) });

    partial void OnSeeThroughOpacityChanged(double value) => Save(s => s with { SeeThroughOpacity = Math.Clamp((int)Math.Round(value), 10, 100) });

    partial void OnNamesChanged(bool value) => Save(s => s with { Names = value });

    private static string Name(IReadOnlyList<(int Value, string Name)> choices, int value) =>
        choices.OrderBy(c => Math.Abs(c.Value - value)).First().Name;

    private static int Value(IReadOnlyList<(int Value, string Name)> choices, string name, int fallback) =>
        choices.FirstOrDefault(c => c.Name == name) is { Name: not null } choice ? choice.Value : fallback;

    private void Save(Func<MapOverlaySettings, MapOverlaySettings> change)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            var current = _services.Settings.Current;
            _services.Settings.Save(current with { MapOverlay = change(current.MapOverlay) });
            Status = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save settings: {ex.Message}";
        }
    }
}
