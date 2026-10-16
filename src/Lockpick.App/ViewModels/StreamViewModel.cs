using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>A widget tile's size on the Stream page.</summary>
public enum TileSize
{
    Small,
    Medium,
    Large,
}

/// <summary>One widget on the Stream page.</summary>
public sealed partial class StreamTile : ObservableObject
{
    private readonly StreamViewModel _page;

    public StreamTile(StreamViewModel page, string id, string name, TileSize size)
    {
        _page = page;
        Id = id;
        Name = name;
        Size = size;
    }

    public string Id { get; }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Width), nameof(Height), nameof(SizeText))]
    public partial TileSize Size { get; set; }

    public double Width => Size switch { TileSize.Small => 280, TileSize.Medium => 380, _ => 560 };

    public double Height => Size switch { TileSize.Small => 220, TileSize.Medium => 320, _ => 480 };

    public string SizeText => Size.ToString();

    public bool Editing => _page.Editing;

    public string PopOutText => _page.Windows.IsOpen(Id) ? "Close window" : "Pop out";

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Editing));
        OnPropertyChanged(nameof(PopOutText));
    }

    [RelayCommand]
    private void Bigger() => _page.Resize(this, Size == TileSize.Large ? TileSize.Small : Size + 1);

    [RelayCommand]
    private void Earlier() => _page.Move(this, -1);

    [RelayCommand]
    private void Later() => _page.Move(this, +1);

    [RelayCommand]
    private void Remove() => _page.Remove(this);

    [RelayCommand]
    private void PopOut() => _page.Windows.Toggle(Id);
}

/// <summary>
/// Stream page (roadmap: widgets off the game too): the overlay's widgets inside Lockpick, as large as the player
/// likes, so the game's picture can stay clean — the tiles they choose, in their order and sizes (remembered). Any
/// tile can also pop out into a window of its own (for another monitor or OBS's window capture).
/// </summary>
public sealed partial class StreamViewModel : PageViewModel
{
    /// <summary>The page's widgets the first time: what a runner watches most, the map largest.</summary>
    private static readonly string[] Defaults = ["status:Medium", "splits:Medium", "minimap:Large", "enemies:Small", "feed:Medium"];

    private readonly AppServices _services;

    public StreamViewModel(AppServices services)
        : base("Stream", "Icon.PanelsTopLeft")
    {
        _services = services;
        Windows = services.WidgetWindows;
        Load(services.Settings.Current.StreamWidgets ?? Defaults);
        RefreshAddable();
        Windows.Changed += (_, _) =>
        {
            foreach (var tile in Tiles)
            {
                tile.Refresh();
            }
        };
    }

    public WidgetWindows Windows { get; }

    public ObservableCollection<StreamTile> Tiles { get; } = [];

    /// <summary>The widgets not on the page yet (by name).</summary>
    public ObservableCollection<string> Addable { get; } = [];

    public bool HasTiles => Tiles.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditText))]
    public partial bool Editing { get; set; }

    public string EditText => Editing ? "Done" : "Customize";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string? ToAdd { get; set; }

    partial void OnEditingChanged(bool value)
    {
        foreach (var tile in Tiles)
        {
            tile.Refresh();
        }
    }

    [RelayCommand]
    private void ToggleEditing() => Editing = !Editing;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (OverlayLayout.Widgets.FirstOrDefault(w => w.Name == ToAdd) is { Id: { } id, Name: { } name })
        {
            Tiles.Add(new StreamTile(this, id, name, TileSize.Medium));
            Changed();
        }
    }

    private bool CanAdd() => ToAdd is not null;

    [RelayCommand]
    private void ResetLayout()
    {
        Tiles.Clear();
        Load(Defaults);
        Changed();
    }

    internal void Resize(StreamTile tile, TileSize size)
    {
        tile.Size = size;
        Changed();
    }

    internal void Move(StreamTile tile, int by)
    {
        var from = Tiles.IndexOf(tile);
        var to = Math.Clamp(from + by, 0, Tiles.Count - 1);
        if (from >= 0 && to != from)
        {
            Tiles.Move(from, to);
            Changed();
        }
    }

    internal void Remove(StreamTile tile)
    {
        Tiles.Remove(tile);
        Changed();
    }

    /// <summary>Tiles from "id:size" entries (unknown widgets and repeats skipped).</summary>
    private void Load(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var parts = entry.Split(':');
            if (OverlayLayout.Widgets.FirstOrDefault(w => w.Id == parts[0]) is { Id: { } id, Name: { } name } && Tiles.All(t => t.Id != id))
            {
                var size = parts.Length > 1 && Enum.TryParse<TileSize>(parts[1], out var s) ? s : TileSize.Medium;
                Tiles.Add(new StreamTile(this, id, name, size));
            }
        }
    }

    private void Changed()
    {
        foreach (var tile in Tiles)
        {
            tile.Refresh();
        }

        RefreshAddable();
        OnPropertyChanged(nameof(HasTiles));
        try
        {
            _services.Settings.Save(_services.Settings.Current with { StreamWidgets = [.. Tiles.Select(t => $"{t.Id}:{t.Size}")] });
        }
        catch (IOException)
        {
            // The layout still applies now; it's only not remembered.
        }
    }

    private void RefreshAddable()
    {
        Addable.Clear();
        foreach (var (id, name, _) in OverlayLayout.Widgets)
        {
            if (Tiles.All(t => t.Id != id))
            {
                Addable.Add(name);
            }
        }

        ToAdd = Addable.FirstOrDefault();
    }
}
