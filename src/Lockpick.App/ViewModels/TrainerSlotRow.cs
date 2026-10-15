using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lockpick.App.ViewModels;

/// <summary>One held item on the Trainer page; items with a count (rounds, ammo, ribbons) can have it changed.</summary>
public sealed partial class TrainerSlotRow : ObservableObject
{
    private readonly Action<TrainerSlotRow> _set;

    public TrainerSlotRow(int slot, string name, int quantity, bool editable, Action<TrainerSlotRow> set)
    {
        Slot = slot;
        Name = name;
        Quantity = quantity;
        Editable = editable;
        Target = quantity;
        _set = set;
    }

    public int Slot { get; }

    public string Name { get; }

    public int Quantity { get; }

    public bool Editable { get; }

    public string Label => Editable ? $"{Slot + 1}. {Name} ×{Quantity}" : $"{Slot + 1}. {Name}";

    [ObservableProperty]
    public partial decimal? Target { get; set; }

    [RelayCommand]
    private void Set() => _set(this);
}
