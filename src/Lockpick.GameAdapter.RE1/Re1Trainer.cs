using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// RE1's trainer features as profile fields: HP (bounded by max HP) and, for infinite ammo, the quantity byte of the
/// equipped slot in the inventory the game is using (Chris's and Jill's at 0xBE9944, Rebecca's at 0xBE9950).
/// </summary>
internal sealed class Re1Trainer(Func<int, string?> itemName) : IGameTrainer
{
    private const int ItemBoxEntries = 48;
    private const int LastItem = 0x4D;      // radio; 0x4E and up are documents and maps
    private const int InkRibbon = 0x2F;
    private const int LastAmmo = 0x12;      // clip 0x0B … flame rounds 0x12
    private const int Knife = 0x01;
    private const int LastWeapon = 0x0A; // Beretta 2 … rocket launcher 10 (B101–B107)
    private const int Rebecca = 3;

    public string HealthField => Re1Session.Fields.Hp;

    public string MaxHealthField => Re1Session.Fields.MaxHp;

    public Func<GameSnapshot, IReadOnlyList<FieldWrite>> CreateInfiniteAmmoRule()
    {
        // The most rounds seen in each slot for the weapon now in it: a shot that lowers the count is put back, a reload
        // raises the mark. A different weapon in the slot starts over from what it holds.
        var highest = new Dictionary<int, (int Item, int Rounds)>();
        return snapshot =>
        {
            if (snapshot.Inventory is not { HasValue: true, Value: { } held }
                || Raw(snapshot, "equippedSlot") is not { } equipped || equipped < 1)
            {
                return [];
            }

            var slot = (int)equipped - 1;
            var weapon = held.FirstOrDefault(h => h.Slot == slot);
            if (weapon is null || weapon.ItemId <= Knife || weapon.ItemId > LastWeapon)
            {
                return [];
            }

            // The item list is refreshed less often than every poll; the slot's quantity byte is read every poll.
            var rebecca = Raw(snapshot, Re1Session.Fields.Character) is { } c && (c & 3) == Rebecca;
            var field = $"{(rebecca ? "rebInv" : "inv")}{slot}Qty";
            if (Raw(snapshot, field) is { } rounds)
            {
                weapon = weapon with { Quantity = (int)rounds };
            }

            if (!highest.TryGetValue(slot, out var mark) || mark.Item != weapon.ItemId || weapon.Quantity > mark.Rounds)
            {
                highest[slot] = (weapon.ItemId, weapon.Quantity);
                return [];
            }

            if (weapon.Quantity >= mark.Rounds)
            {
                return [];
            }

            return [new FieldWrite(field, mark.Rounds)];
        };
    }

    public IReadOnlyList<GivableItem> GivableItems => field ??=
        [.. Enumerable.Range(1, LastItem).Select(id => (id, name: itemName(id))).Where(i => i.name is not null)
            .Select(i => new GivableItem(i.id, i.name!, HasQuantity(i.id)))];

    /// <summary>Weapons (loaded rounds), ammo and ink ribbons have a count; other items are single.</summary>
    public bool HasQuantity(int itemId) => itemId is > Knife and <= LastAmmo or InkRibbon;

    public FieldWrite? SetQuantity(GameSnapshot snapshot, int slot, int quantity, out string? refusal)
    {
        refusal = null;
        if (snapshot.Inventory is not { HasValue: true, Value: { } held } || held.FirstOrDefault(h => h.Slot == slot) is not { } item)
        {
            refusal = $"slot {slot + 1} is empty";
            return null;
        }

        if (!HasQuantity(item.ItemId))
        {
            refusal = $"{itemName(item.ItemId) ?? "this item"} has no count to change";
            return null;
        }

        // A weapon may be empty; ammo and ribbons need at least one (an item with no count left is removed by the game).
        var lowest = item.ItemId <= LastWeapon ? 0 : 1;
        if (quantity < lowest || quantity > byte.MaxValue)
        {
            refusal = $"{quantity} is outside {lowest}..{byte.MaxValue}";
            return null;
        }

        var rebecca = Raw(snapshot, Re1Session.Fields.Character) is { } c && (c & 3) == Rebecca;
        return new FieldWrite($"{(rebecca ? "rebInv" : "inv")}{slot}Qty", quantity);
    }

    public FieldWrite? AddToStorage(GameSnapshot snapshot, int itemId, int quantity, out string? refusal)
    {
        refusal = null;
        if (itemId is < 1 or > LastItem || itemName(itemId) is null)
        {
            refusal = $"item {itemId} isn't one the game knows";
            return null;
        }

        if (snapshot.ItemBox is not { HasValue: true, Value: { } box })
        {
            refusal = "the item box can't be read right now";
            return null;
        }

        quantity = HasQuantity(itemId) ? quantity : 1;
        if (quantity is < 1 or > byte.MaxValue)
        {
            refusal = $"{quantity} is outside 1..{byte.MaxValue}";
            return null;
        }

        var used = box.Select(b => b.Slot).ToHashSet();
        var free = Enumerable.Range(0, ItemBoxEntries).FirstOrDefault(i => !used.Contains(i), -1);
        if (free < 0)
        {
            refusal = "the item box is full";
            return null;
        }

        return new FieldWrite($"box{free}", itemId | (quantity << 8));
    }

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? value.Value : null;
}
