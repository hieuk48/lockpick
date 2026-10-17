using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.Core;

public sealed class RunServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeStateSource _source = new();
    private readonly FakeWriteHost _host = new();
    private readonly TrainerService _trainer;
    private readonly ManualTimeProvider _clock = new(T0);
    private readonly StubRules _rules = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-runs-" + Guid.NewGuid().ToString("N"));
    private readonly RunHistoryStore _history;
    private readonly RunService _runs;
    private readonly List<GameEvent> _events = [];
    private readonly List<RunRecord> _ended = [];

    private static readonly SplitSet Set = new("test", "Test splits", "Jill - Test", PlayableCharacter.Jill,
    [
        new SplitDefinition("Hall", new RoomEntered("106", From: "105")),
        new SplitDefinition("Upstairs", new RoomEntered("200", FromOtherStage: true)),
        new SplitDefinition("Emblem", new ItemObtained(31)),
        new SplitDefinition("End", new RunFinished()),
    ], Verified: false, "test");

    public RunServiceTests()
    {
        _trainer = new TrainerService(_source, _host, new TrainerActionLog(null), NullLogger.Instance);
        _history = new RunHistoryStore(_folder);
        _runs = new RunService(_source, _trainer, _ => _rules, _history, _clock);
        _runs.EventRaised += (_, e) => _events.Add(e);
        _runs.AttemptEnded += (_, r) => _ended.Add(r);
        _source.Attach(1234, TrainerServiceTests.Profile);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _trainer.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static GameSnapshot At(string room, double seconds, params int[] items) => new()
    {
        InGameplay = Field<bool>.Valid(true),
        Location = Field<RoomLocation>.Valid(new RoomLocation(room[0] - '0', Convert.ToInt32(room[1..], 16))),
        GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(seconds)),
        Inventory = Field<IReadOnlyList<HeldItem>>.Valid([.. items.Select((id, i) => new HeldItem(i, id, 1))]),
    };

    private void NewGame()
    {
        _source.Poll(At("105", 0.5));
        _source.Raise(new GameLoaded(_clock.GetUtcNow(), TimeSpan.FromSeconds(0.5), IsNewGame: true));
    }

    private void Play(string room, double seconds, params int[] items)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        _source.Poll(At(room, seconds, items));
    }

    [Fact]
    public void A_run_from_a_save_is_timed_on_the_game_s_clock_unless_asked_to_count_from_the_load()
    {
        _runs.StartOnLoad = true;
        _runs.Choose(Set);
        _source.Poll(At("105", 600));
        _source.Raise(new GameLoaded(_clock.GetUtcNow(), TimeSpan.FromSeconds(600), IsNewGame: false));
        Assert.True(_runs.Status.FromSave);
        Assert.Equal(TimeSpan.FromSeconds(610), _runs.Status.GameTime(At("105", 610))); // the game's own clock

        _runs.Reset();
        _runs.SaveRunsOnGameClock = false;
        _source.Raise(new GameLoaded(_clock.GetUtcNow(), TimeSpan.FromSeconds(600), IsNewGame: false));
        Assert.Equal(TimeSpan.FromSeconds(10), _runs.Status.GameTime(At("105", 610))); // from the load
    }

    [Fact]
    public void Splits_for_the_other_character_are_flagged_at_the_start()
    {
        _runs.Choose(Set); // Jill's splits
        NewGame();
        _source.Poll(At("105", 1) with { Character = Field<PlayableCharacter>.Valid(PlayableCharacter.Chris) });
        Assert.Contains("These are Jill's, but you're playing Chris", _runs.Status.Warning, StringComparison.Ordinal);

        NewGame();
        _source.Poll(At("105", 1) with { Character = Field<PlayableCharacter>.Valid(PlayableCharacter.Jill) });
        Assert.Null(_runs.Status.Warning);
    }

    [Fact]
    public void A_new_game_starts_an_attempt_on_the_game_s_own_clock()
    {
        _runs.Choose(Set);
        Assert.Equal(RunState.Ready, _runs.Status.State);
        NewGame();

        Assert.Equal(RunState.Running, _runs.Status.State);
        Assert.Equal(1, _runs.Status.Attempt);
        Assert.Equal(TimeSpan.Zero, _runs.Status.GameTimeOffset);
        Assert.IsType<RunStarted>(Assert.Single(_events));
    }

    [Fact]
    public void Nothing_starts_until_a_split_set_is_chosen()
    {
        NewGame();
        Assert.Equal(RunState.Off, _runs.Status.State);
    }

    [Fact]
    public void Splits_fire_on_their_own_trigger_in_order()
    {
        _runs.Choose(Set);
        NewGame();
        Play("200", 10);   // not yet: the hall split is first
        Assert.Equal(0, _runs.Status.Current);
        Play("105", 20);
        Play("106", 30);   // 105 → 106
        Assert.Equal(1, _runs.Status.Current);
        Assert.Equal(TimeSpan.FromSeconds(30), _runs.Status.Times[0]!.GameTime);

        Play("107", 40);
        Play("200", 50);   // from another stage
        Assert.Equal(2, _runs.Status.Current);
        Play("201", 60, 31); // the emblem picked up
        Assert.Equal(3, _runs.Status.Current);
        Assert.Equal(TimeSpan.FromSeconds(60), _runs.Status.Times[2]!.GameTime);
    }

    [Fact]
    public void A_room_split_from_a_given_room_ignores_other_ways_in()
    {
        _runs.Choose(Set);
        NewGame();
        Play("107", 10);
        Play("106", 20);   // 107 → 106: not the hall split's door
        Assert.Equal(0, _runs.Status.Current);
    }

    [Fact]
    public void The_ending_finishes_the_run_and_keeps_a_clean_record()
    {
        _runs.Choose(Set);
        NewGame();
        Play("105", 5);
        Play("106", 10);
        Play("200", 20);
        Play("201", 30, 31);
        _rules.Finished = true;
        Play("301", 40, 31);

        Assert.Equal(RunState.Finished, _runs.Status.State);
        Assert.Equal(RunResult.Finished, _runs.Status.Result);
        var record = Assert.Single(_ended);
        Assert.True(record.IsClean);
        Assert.Equal(TimeSpan.FromSeconds(40), record.GameTime);
        Assert.Equal(record, Assert.Single(_history.Load("test")), new RecordComparer());
        Assert.Contains(_events, e => e is RunEnded { Result: RunResult.Finished });
    }

    [Fact]
    public void A_death_ends_the_attempt_and_the_next_new_game_starts_another()
    {
        _runs.Choose(Set);
        NewGame();
        Play("105", 5);
        _rules.Died = true;
        Play("105", 6);
        Assert.Equal(RunState.Ended, _runs.Status.State);
        Assert.Equal(RunResult.Died, _runs.Status.Result);

        _rules.Died = false;
        NewGame();
        Assert.Equal(RunState.Running, _runs.Status.State);
        Assert.Equal(2, _runs.Status.Attempt);
    }

    [Fact]
    public void Deaths_are_counted_once_each_with_or_without_a_run()
    {
        var died = 0;
        _runs.Died += (_, _) => died++;
        _rules.Died = true;
        Play("105", 4);                  // already dead when Lockpick first sees the game: not a new death
        _rules.Died = false;
        Play("105", 5);                  // no run chosen: practice
        _rules.Died = true;
        Play("105", 6);
        Play("105", 7);                  // still dead (the game over screen): the same death
        _rules.Died = false;
        Play("105", 8);
        _runs.Choose(Set);
        NewGame();
        _rules.Died = true;
        Play("105", 9);                  // a death in a run
        Assert.Equal(2, _runs.Deaths);
        Assert.Equal(2, died);
    }

    [Fact]
    public void Attempts_are_numbered_after_the_kept_ones_and_closing_keeps_the_one_running()
    {
        _runs.Choose(Set);
        NewGame();
        _runs.Reset();
        Assert.Equal(1, Assert.Single(_history.Load("test")).Attempt);

        using var again = new RunService(_source, null, _ => _rules, _history, _clock);
        again.Choose(Set);
        _source.Raise(new GameLoaded(T0, TimeSpan.Zero, IsNewGame: true));
        Assert.Equal(2, again.Status.Attempt);
        again.Dispose();
        Assert.Equal(RunResult.LockpickClosed, _history.Load("test")[^1].Result);
    }

    [Fact]
    public void The_title_menu_ends_the_attempt()
    {
        _runs.Choose(Set);
        NewGame();
        _rules.Title = true;
        Play("105", 5);
        Assert.Equal(RunResult.QuitToTitle, _runs.Status.Result);
    }

    [Fact]
    public void Split_undo_and_skip_keys()
    {
        _runs.Choose(Set);
        NewGame();
        Play("105", 5);
        _runs.Split();
        Assert.Equal(1, _runs.Status.Current);
        _runs.Undo();
        Assert.Equal(0, _runs.Status.Current);
        Assert.Null(_runs.Status.Times[0]);
        _runs.Skip();
        Assert.Equal(1, _runs.Status.Current);
        Assert.Null(_runs.Status.Times[0]);
    }

    [Fact]
    public void A_run_from_a_save_starts_only_when_asked()
    {
        _runs.Choose(Set);
        _source.Raise(new GameLoaded(T0, TimeSpan.FromMinutes(20), IsNewGame: false));
        Assert.Equal(RunState.Ready, _runs.Status.State);

        _runs.StartOnLoad = true;
        _source.Raise(new GameLoaded(T0, TimeSpan.FromMinutes(20), IsNewGame: false));
        Assert.Equal(RunState.Running, _runs.Status.State);
        Assert.True(_runs.Status.FromSave);
    }

    [Fact]
    public void Damage_is_counted_and_the_game_closing_ends_the_attempt()
    {
        _runs.Choose(Set);
        NewGame();
        _source.Raise(new PlayerDamaged(T0, 10, 86));
        _source.Raise(new PlayerDamaged(T0, 12, 74));
        _source.Raise(new GameStopped(T0, "exited"));
        Assert.Equal(22, Assert.Single(_ended).DamageTaken);
        Assert.Equal(RunResult.GameClosed, _ended[0].Result);
    }

    [Fact]
    public void A_record_s_summary_has_the_time_the_splits_and_what_timed_it()
    {
        var record = new RunRecord("s", "S", "Jill Any% NMG", PlayableCharacter.Jill, 3, T0, T0.AddMinutes(70), RunResult.Finished,
            new TimeSpan(1, 2, 3) + TimeSpan.FromMilliseconds(450), TimeSpan.FromMinutes(70),
            [new RecordedSplit("Emblem", TimeSpan.FromSeconds(75), null), new RecordedSplit("End", new TimeSpan(1, 2, 3), null)],
            false, 22, false, "re1-gog-usa-hotfix4", "205BD6FE");
        var summary = record.Summary("Resident Evil (PC)", "Lockpick 0.3.0");

        Assert.Contains("Resident Evil (PC) — Jill Any% NMG — finished in 1:02:03.45 in-game time", summary, StringComparison.Ordinal);
        Assert.Contains("Emblem 1:15.00", summary, StringComparison.Ordinal);
        Assert.Contains("Timed by Lockpick 0.3.0 on re1-gog-usa-hotfix4, ResidentEvil.exe SHA-256 205BD6FE; read-only, the trainer was off", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_personal_best_and_best_segments_come_from_clean_attempts()
    {
        static RunRecord Run(RunResult result, bool cheats, params double[] splits) => new("s", "S", "C", PlayableCharacter.Jill, 1, T0, T0,
            result, TimeSpan.FromSeconds(splits[^1]), TimeSpan.Zero,
            [.. splits.Select((t, i) => new RecordedSplit($"{i}", TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t)))], false, 0, cheats, null);

        var comparison = RunComparison.From(
        [
            Run(RunResult.Finished, false, 10, 30, 60),
            Run(RunResult.Finished, false, 12, 25, 55),
            Run(RunResult.Finished, true, 1, 2, 3),       // the trainer was used: ignored
            Run(RunResult.Died, false, 8, 40, 70),        // not finished, but its first segment is the best
        ], 3);

        Assert.Equal([TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(55)], comparison.PersonalBest);
        Assert.Equal([TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(30)], comparison.BestSegments);
        Assert.Equal(TimeSpan.FromSeconds(51), comparison.SumOfBest);
    }

    private sealed class StubRules : IRunRules
    {
        public bool Died { get; set; }

        public bool Finished { get; set; }

        public bool Title { get; set; }

        public IReadOnlyList<SplitSet> SplitSets => [Set];

        public bool PlayerDied(GameSnapshot snapshot) => Died;

        public bool RunFinished(GameSnapshot previous, GameSnapshot current) => Finished;

        public bool AtTitle(GameSnapshot snapshot) => Title;
    }

    /// <summary>Records compare by value except their split lists (lists compare by reference).</summary>
    private sealed class RecordComparer : IEqualityComparer<RunRecord>
    {
        private static readonly IReadOnlyList<RecordedSplit> None = [];

        public bool Equals(RunRecord? x, RunRecord? y) =>
            x is not null && y is not null && (x with { Splits = None }) == (y with { Splits = None }) && x.Splits.SequenceEqual(y.Splits);

        public int GetHashCode(RunRecord obj) => obj.SetId.GetHashCode(StringComparison.Ordinal);
    }
}
