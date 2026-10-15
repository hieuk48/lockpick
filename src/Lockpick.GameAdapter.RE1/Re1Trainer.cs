using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// RE1's trainer features as profile fields: HP (bounded by max HP) and, for infinite ammo, the quantity byte of the
/// equipped slot in the inventory the game is using (Chris's and Jill's at 0xBE9944, Rebecca's at 0xBE9950).
/// </summary>
internal sealed class Re1Trainer : IGameTrainer
{
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

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? value.Value : null;
}
