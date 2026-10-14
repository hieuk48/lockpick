using Lockpick.Core.Insight;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

/// <summary>RE1's ask-mode knowledge: what the room index and confirmed labels explain, and what gets asked about.</summary>
public class Re1ChangeExplainerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly IChangeExplainer _explainer;

    public Re1ChangeExplainerTests()
    {
        var adapter = new Re1Adapter();
        _explainer = adapter.CreateChangeExplainer(adapter.Profiles[0]);
    }

    /// <summary>Jill in the dining room (105), the values seen live before she took the blue jewel.</summary>
    private static GameSnapshot Jill(double seconds, string room = "105", params (string Field, long Value)[] raw)
    {
        var fields = new Dictionary<string, Field<long>>
        {
            ["character"] = Field<long>.Valid(1),
            ["flagRoomItems1"] = Field<long>.Valid(0xFEDFFFFF),
            ["flagScenarioB0"] = Field<long>.Valid(0x80000000),
            ["flagRoom0"] = Field<long>.Valid(0x06000000),
            ["flagRoom3"] = Field<long>.Valid(0),
            ["inv0"] = Field<long>.Valid(0x0001),
        };
        foreach (var (field, value) in raw)
        {
            fields[field] = Field<long>.Valid(value);
        }

        return new GameSnapshot
        {
            CapturedAt = T0.AddSeconds(seconds),
            InGameplay = Field<bool>.Valid(true),
            Location = Field<RoomLocation>.Valid(new RoomLocation(room[0] - '0', Convert.ToInt32(room[1..], 16))),
            GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(100 + seconds)),
            RawFields = fields,
        };
    }

    [Fact]
    public void Pickups_from_the_room_index_are_explained_and_named()
    {
        var after = Jill(1, raw: ("flagRoomItems1", 0xFED7FFFF)); // blue jewel, item flag 44
        var report = _explainer.Explain(Jill(0), after);
        Assert.Empty(report.Unexplained);
        Assert.Equal(["picked up Blue jewel"], report.Known);

        // Putting it back (the Emblem trick) is explained too.
        Assert.Equal(["put Blue jewel back"], _explainer.Explain(after, Jill(2)).Known);
    }

    [Fact]
    public void Story_flags_are_unexplained_unless_confirmed()
    {
        var change = Assert.Single(_explainer.Explain(Jill(0), Jill(1, raw: ("flagScenarioB0", 0xC0000000))).Unexplained);
        Assert.Equal("flagScenarioB0:0x40000000:set", change.Key);
        Assert.Contains("story B flag #1 set", change.Description, StringComparison.Ordinal);

        // The statue falling is a confirmed label: not asked about.
        Assert.Empty(_explainer.Explain(Jill(0, "202"), Jill(1, "202", ("flagScenarioA0", 0x00100000))).Unexplained);
    }

    [Fact]
    public void First_visits_are_explained_but_other_room_bank_bits_are_not()
    {
        Assert.Empty(_explainer.Explain(Jill(0, "104"), Jill(1, "104", ("flagRoom0", 0x0E000000))).Unexplained); // room 104 = bit 27
        // Visits are numbered on across stages: the courtyard (300) is flag 63, and the map's own room numbers count
        // (107's back room is map room 11D, flag 29).
        Assert.Equal(["first visit to room 300"], _explainer.Explain(Jill(0, "300", ("flagRoom1", 0)), Jill(1, "300", ("flagRoom1", 0x1))).Known);
        Assert.Equal(["first visit to room 11D"], _explainer.Explain(Jill(0, "107"), Jill(1, "107", ("flagRoom0", 0x06000004))).Known);

        // Beyond the visits come maps and documents: an unlabelled one is asked about.
        var odd = Assert.Single(_explainer.Explain(Jill(0, "107", ("flagRoom4", 0)), Jill(1, "107", ("flagRoom4", 0x1))).Unexplained);
        Assert.Equal("flagRoom4:0x00000001:set", odd.Key);
    }

    [Fact]
    public void Unknown_items_and_enemies_are_reported()
    {
        var changes = _explainer.Explain(Jill(0), Jill(1, raw: [("inv3", 0x014C), ("enemy0Flags", 1), ("enemy0Type", 60)])).Unexplained;
        Assert.Contains(changes, c => c.Key == "item:0x4C");
        Assert.Contains(changes, c => c.Key == "enemy:60");
    }

    [Fact]
    public void Nothing_is_reported_across_a_new_game_or_load()
    {
        var later = Jill(0, raw: ("flagScenarioB0", 0xFFFFFFFF));
        var newGame = Jill(1) with { GameTime = Field<TimeSpan>.Valid(TimeSpan.Zero) };
        Assert.Empty(_explainer.Explain(later, newGame).Unexplained);
    }
}
