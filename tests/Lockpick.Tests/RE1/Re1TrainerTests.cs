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
    public void Counts_change_only_on_weapons_ammo_and_ribbons_within_bounds()
    {
        var game = Holding(0, 1, new HeldItem(0, 0x02, 15), new HeldItem(1, 0x0B, 15), new HeldItem(2, 0x1F, 1));

        Assert.Equal(new FieldWrite("inv0Qty", 50), _trainer.SetQuantity(game, 0, 50, out _));
        Assert.Equal(new FieldWrite("inv0Qty", 0), _trainer.SetQuantity(game, 0, 0, out _));   // an empty gun is fine
        Assert.Null(_trainer.SetQuantity(game, 1, 0, out var why));                             // ammo needs at least one
        Assert.Contains("outside", why, StringComparison.Ordinal);
        Assert.Null(_trainer.SetQuantity(game, 2, 5, out why));                                 // the Emblem has no count
        Assert.Null(_trainer.SetQuantity(game, 5, 5, out why));                                 // empty slot
        Assert.Null(_trainer.SetQuantity(game, 0, 256, out _));
    }

    [Fact]
    public void Items_go_into_the_first_free_item_box_entry()
    {
        var game = Holding(0, 1) with
        {
            ItemBox = Field<IReadOnlyList<HeldItem>>.Valid([new HeldItem(0, 0x0B, 15), new HeldItem(1, 0x0B, 15), new HeldItem(3, 0x44, 1)]),
        };

        Assert.Equal(new FieldWrite("box2", 0x0C | (30 << 8)), _trainer.AddToStorage(game, 0x0C, 30, out _)); // 30 shells
        Assert.Equal(new FieldWrite("box2", 0x41 | (1 << 8)), _trainer.AddToStorage(game, 0x41, 9, out _));   // a spray is single
        Assert.Null(_trainer.AddToStorage(game, 0x4E, 1, out var why));                                       // a document
        Assert.NotNull(why);
        Assert.Contains(_trainer.GivableItems, i => i.Name == "Shells" && i.HasQuantity);

        var full = game with { ItemBox = Field<IReadOnlyList<HeldItem>>.Valid([.. Enumerable.Range(0, 48).Select(i => new HeldItem(i, 0x0B, 1))]) };
        Assert.Null(_trainer.AddToStorage(full, 0x0C, 5, out why));
        Assert.Contains("full", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shot_is_the_same_gun_losing_rounds_not_a_reload_or_the_knife()
    {
        var rules = (Lockpick.Core.Challenges.IChallengeRules)_trainer;
        var before = Holding(2, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 15));
        Assert.Equal("Beretta", rules.NonMeleeShot(before, Holding(2, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 14))));
        Assert.Null(rules.NonMeleeShot(before, Holding(2, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 15))));    // nothing
        Assert.Null(rules.NonMeleeShot(Holding(2, 1, new HeldItem(1, 0x02, 3)), Holding(2, 1, new HeldItem(1, 0x02, 15)))); // reload
        Assert.Null(rules.NonMeleeShot(Holding(1, 1, new HeldItem(0, 0x01, 0)), Holding(1, 1, new HeldItem(0, 0x01, 0)))); // knife
        Assert.Null(rules.NonMeleeShot(before, Holding(1, 1, new HeldItem(0, 0x01, 0), new HeldItem(1, 0x02, 14))));     // switched
    }

    [Fact]
    public void Rebecca_uses_her_own_inventory()
    {
        var rule = _trainer.CreateInfiniteAmmoRule();
        rule(Holding(1, 3, new HeldItem(0, 0x02, 15)));
        Assert.Equal([new FieldWrite("rebInv0Qty", 15)], rule(Holding(1, 3, new HeldItem(0, 0x02, 13))));
    }
}
