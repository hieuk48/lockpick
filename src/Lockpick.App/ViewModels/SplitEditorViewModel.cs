using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>An item to pick in the split editor.</summary>
public sealed record ItemChoice(int Id, string Name)
{
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>One split being edited: its name and what triggers it.</summary>
public sealed partial class SplitEditRow : ObservableObject
{
    public const string Room = "Entering a room";
    public const string Item = "Getting an item";
    public const string Ending = "The ending";
    public const string Key = "The split key";

    private readonly SplitEditorViewModel _editor;

    public SplitEditRow(SplitEditorViewModel editor, SplitDefinition split)
    {
        _editor = editor;
        Name = split.Name;
        switch (split.Trigger)
        {
            case RoomEntered r:
                Kind = Room;
                RoomId = r.Room;
                FromRoom = r.From ?? string.Empty;
                OtherArea = r.FromOtherStage;
                break;
            case ItemObtained i:
                Kind = Item;
                ItemPick = editor.Items.FirstOrDefault(c => c.Id == i.ItemId) ?? new ItemChoice(i.ItemId, $"item {i.ItemId}");
                ItemRoom = i.Room ?? string.Empty;
                break;
            case RunFinished:
                Kind = Ending;
                break;
            default:
                Kind = Key;
                break;
        }
    }

    public static IReadOnlyList<string> Kinds { get; } = [Room, Item, Ending, Key];

    public IReadOnlyList<ItemChoice> Items => _editor.Items;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoom), nameof(IsItem))]
    public partial string Kind { get; set; } = Key;

    [ObservableProperty]
    public partial string RoomId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FromRoom { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool OtherArea { get; set; }

    [ObservableProperty]
    public partial ItemChoice? ItemPick { get; set; }

    [ObservableProperty]
    public partial string ItemRoom { get; set; } = string.Empty;

    public bool IsRoom => Kind == Room;

    public bool IsItem => Kind == Item;

    [RelayCommand]
    private void Up() => _editor.Move(this, -1);

    [RelayCommand]
    private void Down() => _editor.Move(this, +1);

    [RelayCommand]
    private void Remove() => _editor.Remove(this);

    /// <summary>The split as edited, or why it can't be saved.</summary>
    public (SplitDefinition? Split, string? Problem) Build()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            return (null, "Every split needs a name.");
        }

        switch (Kind)
        {
            case Room:
                if (!SplitEditorViewModel.IsRoom(RoomId))
                {
                    return (null, $"{name}: the room is a stage digit and two hex digits, like 203 or 60B.");
                }

                if (FromRoom.Trim().Length > 0 && !SplitEditorViewModel.IsRoom(FromRoom))
                {
                    return (null, $"{name}: the room it's entered from is like 204 (or leave it empty).");
                }

                return (new SplitDefinition(name, new RoomEntered(RoomId.Trim().ToUpperInvariant(),
                    FromRoom.Trim().Length > 0 ? FromRoom.Trim().ToUpperInvariant() : null, OtherArea)), null);
            case Item:
                if (ItemPick is null)
                {
                    return (null, $"{name}: pick the item.");
                }

                if (ItemRoom.Trim().Length > 0 && !SplitEditorViewModel.IsRoom(ItemRoom))
                {
                    return (null, $"{name}: the item's room is like 719 (or leave it empty for anywhere).");
                }

                return (new SplitDefinition(name, new ItemObtained(ItemPick.Id, ItemRoom.Trim().Length > 0 ? ItemRoom.Trim().ToUpperInvariant() : null)), null);
            case Ending:
                return (new SplitDefinition(name, new RunFinished()), null);
            default:
                return (new SplitDefinition(name, new ManualSplit()), null);
        }
    }
}

/// <summary>
/// The split editor (Runs → Edit splits, #57): rename splits and choose what triggers each — entering a room (from a
/// given room, or from another area), getting an item (anywhere or in a given room), the ending, or the split key —
/// add, remove and reorder them, and save the result as the player's own splits. Built-in and imported sets are never
/// changed; editing one saves a copy.
/// </summary>
public sealed partial class SplitEditorViewModel : ObservableObject
{
    private static readonly Regex RoomPattern = new("^[0-9][0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    private readonly RunLibrary _library;
    private SplitSet? _source;

    public SplitEditorViewModel(RunLibrary library)
    {
        _library = library;
        Items = [.. Enumerable.Range(1, 120).Select(id => (id, name: library.Adapter.ItemName(id))).Where(x => x.name is not null)
            .Select(x => new ItemChoice(x.id, x.name!))];
    }

    public IReadOnlyList<ItemChoice> Items { get; }

    public ObservableCollection<SplitEditRow> Rows { get; } = [];

    public IReadOnlyList<PlayableCharacter> Characters { get; } = [PlayableCharacter.Jill, PlayableCharacter.Chris];

    [ObservableProperty]
    public partial string SetName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PlayableCharacter Character { get; set; } = PlayableCharacter.Jill;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Choose splits on the Splits section first.";

    public bool HasSet => _source is not null;

    public static bool IsRoom(string text) => RoomPattern.IsMatch(text.Trim());

    /// <summary>Edits <paramref name="set"/> (a copy when it isn't the player's own).</summary>
    public void Load(SplitSet? set)
    {
        _source = set;
        Rows.Clear();
        Message = string.Empty;
        OnPropertyChanged(nameof(HasSet));
        if (set is null)
        {
            Heading = "Choose splits on the Splits section first.";
            return;
        }

        var own = set.Source == SplitSetFile.Custom;
        SetName = own ? set.Name : $"My {set.Name}";
        Character = set.Character is PlayableCharacter.Chris ? PlayableCharacter.Chris : PlayableCharacter.Jill;
        Heading = own
            ? $"Editing your splits \"{set.Name}\"."
            : $"Editing a copy of \"{set.Name}\": saving makes your own splits; the {(set.ImportedFrom is null ? "built-in" : "imported")} ones stay as they are.";
        foreach (var split in set.Splits)
        {
            Rows.Add(new SplitEditRow(this, split));
        }
    }

    internal void Move(SplitEditRow row, int by)
    {
        var from = Rows.IndexOf(row);
        var to = Math.Clamp(from + by, 0, Rows.Count - 1);
        if (from >= 0 && to != from)
        {
            Rows.Move(from, to);
        }
    }

    internal void Remove(SplitEditRow row)
    {
        if (Rows.Count > 1)
        {
            Rows.Remove(row);
        }
    }

    [RelayCommand]
    private void AddSplit() => Rows.Add(new SplitEditRow(this, new SplitDefinition($"Split {Rows.Count + 1}", new ManualSplit())));

    [RelayCommand]
    private void Revert() => Load(_source);

    [RelayCommand]
    private void Save()
    {
        if (_source is not { } source)
        {
            return;
        }

        if (SetName.Trim().Length == 0)
        {
            Message = "Give your splits a name.";
            return;
        }

        List<SplitDefinition> splits = [];
        foreach (var row in Rows)
        {
            var (split, problem) = row.Build();
            if (problem is not null)
            {
                Message = problem;
                return;
            }

            splits.Add(split!);
        }

        try
        {
            var saved = _library.SaveCustom(source with { Splits = splits, Character = Character, ImportedFrom = null }, SetName);
            _library.Choose(saved);
            Load(saved);
            Message = $"Saved \"{saved.Name}\" with {saved.Splits.Count} splits, and chose it for your runs.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't save: {ex.Message}";
        }
    }
}
