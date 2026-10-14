using Lockpick.Core.Insight;
using Lockpick.Core.State;

namespace Lockpick.Tests.Core;

public class QuestionSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Reports every key listed in the current snapshot's "changes" raw field names.</summary>
    private sealed class ScriptedExplainer : IChangeExplainer
    {
        public Dictionary<int, string[]> ByPoll { get; } = [];

        public Dictionary<int, string> KnownByPoll { get; } = [];

        public Dictionary<int, string> ActionByPoll { get; } = [];

        public ChangeReport Explain(GameSnapshot previous, GameSnapshot current) => new(
            ByPoll.TryGetValue((int)current.Sequence, out var keys) ? [.. keys.Select(k => new UnexplainedChange(k, k, ByAction: k.StartsWith("story", StringComparison.Ordinal)))] : [],
            KnownByPoll.TryGetValue((int)current.Sequence, out var known) ? [known] : [])
        {
            Actions = ActionByPoll.TryGetValue((int)current.Sequence, out var action) ? [action] : [],
        };
    }

    private static GameSnapshot Poll(int n, bool playing = true, bool timerRuns = true) => new()
    {
        Sequence = n,
        CapturedAt = T0.AddSeconds(n),
        InGameplay = Field<bool>.Valid(playing),
        Location = Field<RoomLocation>.Valid(new RoomLocation(1, 5)),
        GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(timerRuns ? 100 + n : 100)),
    };

    [Fact]
    public void Asks_once_the_changes_stop_and_play_resumes()
    {
        var explainer = new ScriptedExplainer { ByPoll = { [1] = ["a"], [2] = ["b"] }, KnownByPoll = { [1] = "picked up Emblem" } };
        var scheduler = new QuestionScheduler(explainer) { QuietPeriod = TimeSpan.FromSeconds(3) };
        Assert.Null(scheduler.Feed(Poll(0)));
        Assert.Null(scheduler.Feed(Poll(1)));
        Assert.Null(scheduler.Feed(Poll(2)));
        Assert.Null(scheduler.Feed(Poll(4))); // only 2 s quiet
        var question = scheduler.Feed(Poll(5));
        Assert.NotNull(question);
        Assert.Equal(["a", "b"], question.Changes.Select(c => c.Key));
        Assert.Equal("105", question.Room);
        Assert.Equal(T0.AddSeconds(1), question.At);
        Assert.Equal(["1:41 picked up Emblem"], question.Context);
    }

    [Fact]
    public void Story_changes_next_to_a_player_action_are_attributed_not_asked()
    {
        // Poll 1: a crest is used; poll 3: a story flag changes (2 s later) and an unrelated lock flag too.
        var explainer = new ScriptedExplainer { ActionByPoll = { [1] = "used Star crest" }, ByPoll = { [3] = ["story A #106", "lock #40"] } };
        var scheduler = new QuestionScheduler(explainer);
        for (var n = 0; n <= 6; n++)
        {
            if (scheduler.Feed(Poll(n)) is { } question)
            {
                Assert.Equal(["lock #40"], question.Changes.Select(c => c.Key)); // only non-story changes are still asked
            }
        }

        var attributed = Assert.Single(scheduler.TakeAttributed());
        Assert.Equal(("used Star crest", "story A #106"), (attributed.Action, attributed.Change.Key));
        Assert.Empty(scheduler.TakeAttributed());

        // Far from any action, a story change is asked about.
        var lonely = new QuestionScheduler(new ScriptedExplainer { ActionByPoll = { [1] = "used Star crest" }, ByPoll = { [20] = ["story A #3"] } });
        PendingQuestion? asked = null;
        for (var n = 0; n <= 25; n++)
        {
            asked ??= lonely.Feed(Poll(n));
        }

        Assert.Equal(["story A #3"], asked!.Changes.Select(c => c.Key));
    }

    [Fact]
    public void Waits_while_a_cutscene_or_menu_holds_the_timer_and_asks_each_change_once()
    {
        var explainer = new ScriptedExplainer { ByPoll = { [1] = ["a"], [10] = ["a"] } };
        var scheduler = new QuestionScheduler(explainer);
        scheduler.Feed(Poll(0));
        scheduler.Feed(Poll(1));
        Assert.Null(scheduler.Feed(Poll(6, timerRuns: false)));
        Assert.Null(scheduler.Feed(Poll(7, playing: false)));
        Assert.NotNull(scheduler.Feed(Poll(8)));
        scheduler.Feed(Poll(10));
        Assert.Null(scheduler.Feed(Poll(20))); // "a" again: already asked
    }
}
