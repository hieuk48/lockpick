using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

public class Re1TrainerTests
{
    private readonly IGameTrainer _trainer;

    public Re1TrainerTests()
    {
        var adapter = new Re1Adapter();
        _trainer = adapter.CreateTrainer(adapter.Profiles[0]);
    }

    private static GameSnapshot Holding(int equippedSlot, int character, params HeldItem[] items) => new()
    {
        InGameplay = Field<bool>.Valid(true),
        Inventory = Field<IReadOnlyList<HeldItem>>.Valid(items),
        RawFields = new Dictionary<string, Field<long>>
        {
            ["equippedSlot"] = Field<long>.Valid(equippedSlot),
            ["character"] = Field<long>.Valid(character),
        },
    };

    [Fact]
    public void Health_is_hp_bounded_by_max_hp()
    {
        Assert.Equal("hp", _trainer.HealthField);
        Assert.Equal("maxHp", _trainer.MaxHealthField);
    }

    [Fact]
    public void A_shot_is_put_back_and_a_reload_raises_the_mark()
    {
        var rule = _trainer.CreateInfiniteAmmoRule();
        Assert.Empty(rule(Holding(2, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 15)))); // first sight: 15

        var writes = rule(Holding(2, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 14)));  // a shot
        Assert.Equal([new FieldWrite("inv1Qty", 15)], writes);

        var shotgun = _trainer.CreateInfiniteAmmoRule();
        Assert.Empty(shotgun(Holding(1, 1, new HeldItem(0, 0x03, 5))));
        Assert.Empty(shotgun(Holding(1, 1, new HeldItem(0, 0x03, 7))));                           // reloaded to 7
        Assert.Equal([new FieldWrite("inv0Qty", 7)], shotgun(Holding(1, 1, new HeldItem(0, 0x03, 6))));
    }

    [Fact]
    public void Another_weapon_in_the_slot_starts_over_and_the_knife_or_nothing_equipped_is_left_alone()
    {
        var rule = _trainer.CreateInfiniteAmmoRule();
        rule(Holding(1, 0, new HeldItem(0, 0x07, 6)));                                            // bazooka, explosive
        Assert.Empty(rule(Holding(1, 0, new HeldItem(0, 0x08, 2))));                              // now acid rounds: 2 is its mark

        Assert.Empty(rule(Holding(1, 0, new HeldItem(0, 0x01, 0))));                              // knife
        Assert.Empty(rule(Holding(0, 0, new HeldItem(0, 0x02, 3))));                              // nothing equipped
    }

    [Fact]
    public void The_slot_quantity_read_every_poll_wins_over_an_older_item_list()
    {
        var rule = _trainer.CreateInfiniteAmmoRule();
        rule(Holding(1, 0, new HeldItem(0, 0x02, 15)));
        var stale = Holding(1, 0, new HeldItem(0, 0x02, 14));          // the list still says 14 …
        var fresh = stale with { RawFields = new Dictionary<string, Field<long>>(stale.RawFields) { ["inv0Qty"] = Field<long>.Valid(15) } };
        Assert.Empty(rule(fresh));                                      // … but the byte already reads 15 again
    }

    [Fact]
    public void Rebecca_uses_her_own_inventory()
    {
        var rule = _trainer.CreateInfiniteAmmoRule();
        rule(Holding(1, 3, new HeldItem(0, 0x02, 15)));
        Assert.Equal([new FieldWrite("rebInv0Qty", 15)], rule(Holding(1, 3, new HeldItem(0, 0x02, 13))));
    }
}
