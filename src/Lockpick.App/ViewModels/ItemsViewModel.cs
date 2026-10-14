using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>One inventory slot or item-box entry: the item's name and its quantity or load (empty slots have no name).</summary>
public sealed record ItemRow(string Name, string Detail, bool Equipped)
{
    public bool IsEmpty => Name.Length == 0;
}

/// <summary>
/// Items page: what the player carries and what is in the item box, read from the game (read-only). Names come from
/// the adapter; a weapon's quantity is the rounds loaded.
/// </summary>
public sealed partial class ItemsViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(500);

    private readonly AppServices _services;
    private IReadOnlyList<HeldItem>? _lastInventory;
    private IReadOnlyList<HeldItem>? _lastBox;
    private long _lastEquipped = -1;
    private bool _visible;
    private DateTimeOffset _lastUpdate;

    public ItemsViewModel(AppServices services, LiveState state)
        : base("Items", "Icon.Package")
    {
        _services = services;
        State = state;
        state.PropertyChanged += OnStateChanged;
    }

    public LiveState State { get; }

    public ObservableCollection<ItemRow> Inventory { get; } = [];

    public ObservableCollection<ItemRow> Box { get; } = [];

    [ObservableProperty]
    public partial string InventoryHeading { get; private set; } = "Start or load a game to see your items.";

    [ObservableProperty]
    public partial string BoxHeading { get; private set; } = string.Empty;

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

    private string Name(int id) => _services.Adapter.ItemName(id) ?? $"Item {id}";

    private static string Detail(HeldItem item) => item.ItemId switch
    {
        1 => string.Empty,                                   // knife
        6 => $"fuel {item.Quantity}",                        // flamethrower
        >= 2 and <= 10 => $"{item.Quantity} loaded",         // guns and launchers: rounds in the weapon
        _ => item.Quantity > 1 ? $"×{item.Quantity}" : string.Empty,
    };

    private void Refresh(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastUpdate < Throttle)
        {
            return;
        }

        _lastUpdate = now;
        if (State.Snapshot is not { } s || !s.Inventory.TryGet(out var inventory))
        {
            return;
        }

        var equipped = s.RawFields.TryGetValue("equippedSlot", out var e) && e.HasValue ? e.Value : 0;
        if (force || _lastInventory is null || !inventory.SequenceEqual(_lastInventory) || equipped != _lastEquipped)
        {
            _lastInventory = inventory;
            _lastEquipped = equipped;
            Inventory.Clear();
            // The status screen's grid: 2 columns, 8 slots for Jill and 6 for Chris or Rebecca; empty slots shown.
            var slots = s.Character.TryGet(out var who) && who == PlayableCharacter.Jill ? 8 : 6;
            for (var slot = 0; slot < slots; slot++)
            {
                var item = inventory.FirstOrDefault(i => i.Slot == slot);
                // The equipped slot is 1-based (0 = nothing equipped).
                Inventory.Add(item is null
                    ? new ItemRow(string.Empty, string.Empty, false)
                    : new ItemRow(Name(item.ItemId), Detail(item), item.Slot + 1 == equipped));
            }

            InventoryHeading = inventory.Count == 0 ? "Nothing carried" : $"{inventory.Count} items carried";
        }

        if (s.ItemBox.TryGet(out var box) && (force || _lastBox is null || !box.SequenceEqual(_lastBox)))
        {
            _lastBox = box;
            Box.Clear();
            foreach (var item in box)
            {
                Box.Add(new ItemRow(Name(item.ItemId), Detail(item), false));
            }

            BoxHeading = box.Count == 0 ? "The item box is empty" : $"{box.Count} items in the box";
        }
    }
}
