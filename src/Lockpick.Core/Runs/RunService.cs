using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.Core.Runs;

public enum RunState
{
    /// <summary>No split set chosen: runs are off.</summary>
    Off,

    /// <summary>A split set is chosen; the next new game starts an attempt.</summary>
    Ready,

    /// <summary>An attempt is being timed.</summary>
    Running,

    /// <summary>The attempt reached its end (the ending, or its last split).</summary>
    Finished,

    /// <summary>The attempt ended early: a death, the title menu, the game closing or a reset.</summary>
    Ended,
}

public enum RunResult
{
    Finished,
    Died,
    QuitToTitle,
    GameClosed,
    Reset,

    /// <summary>Lockpick was closed during the attempt.</summary>
    LockpickClosed,
}

/// <summary>When a split was reached: the game's clock (the run's in-game time) and real time since the start.</summary>
public sealed record SplitTime(TimeSpan GameTime, TimeSpan RealTime);

/// <summary>The current attempt (or the last one, once it ended).</summary>
public sealed record RunStatus(
    RunState State,
    SplitSet? Set,
    int Attempt,
    int Current,
    IReadOnlyList<SplitTime?> Times,
    TimeSpan GameTimeOffset,
    DateTimeOffset? StartedAt,
    bool FromSave,
    int DamageTaken,
    bool CheatsUsed,
    RunResult? Result,
    DateTimeOffset? EndedAt,
    TimeSpan? EndGameTime)
{
    public static RunStatus Off { get; } = new(RunState.Off, null, 0, 0, [], TimeSpan.Zero, null, false, 0, false, null, null, null);

    /// <summary>What was checked when the attempt began (e.g. the leaderboard checks), for its record; null when nothing.</summary>
    public string? Checks { get; init; }

    /// <summary>Something the player should know about this attempt (e.g. splits for the other character), or null.</summary>
    public string? Warning { get; init; }

    /// <summary>The run's in-game time at <paramref name="snapshot"/> (the game's clock less the start, for a run
    /// started from a save), or the time it ended at.</summary>
    public TimeSpan? GameTime(GameSnapshot? snapshot) =>
        State == RunState.Running
            ? snapshot is { GameTime.HasValue: true } s ? s.GameTime.Value - GameTimeOffset : null
            : EndGameTime;

    /// <summary>Real time since the start (to the end once it ended).</summary>
    public TimeSpan? RealTime(DateTimeOffset now) =>
        StartedAt is { } start ? (EndedAt ?? now) - start : null;
}

/// <summary>
/// Times runs against a split set: an attempt starts on a new game (or, when asked, a loaded save), each split ends on
/// its trigger (only the current split's trigger is checked, as in LiveSplit), and the attempt ends at the ending, a
/// death, the title menu or the game closing. Every attempt is kept as a <see cref="RunRecord"/>. Read-only: it only
/// watches. Using the trainer during an attempt marks it as cheated.
/// </summary>
public sealed class RunService : IDisposable
{
    private readonly IGameStateSource _source;
    private readonly TrainerService? _trainer;
    private readonly Func<VersionProfile, IRunRules?> _rulesFor;
    private readonly RunHistoryStore? _history;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private GameSnapshot? _previous;
    private bool _characterChecked;
    private bool? _dead; // null: not known yet (just attached), so a death already on screen isn't counted
    private RoomLocation? _room;
    private IReadOnlyList<HeldItem>? _inventory;

    // After a split, the next item split also counts an item already held (picked up out of the route's order), as
    // LiveSplit's RE1 autosplitter does: checked once, on the first poll after the split, so a run never waits for a
    // pickup that already happened.
    private bool _heldCheckNext;
    private bool _heldCheck;
    private TimeSpan? _gameTime;
    private RunRecord? _pendingRecord;

    public RunService(IGameStateSource source, TrainerService? trainer, Func<VersionProfile, IRunRules?> rulesFor,
        RunHistoryStore? history = null, TimeProvider? time = null)
    {
        _source = source;
        _trainer = trainer;
        _rulesFor = rulesFor;
        _history = history;
        _time = time ?? TimeProvider.System;
        _source.Polled += OnPolled;
        _source.EventRaised += OnEvent;
        if (_trainer is not null)
        {
            _trainer.EventRaised += OnTrainerEvent;
        }
    }

    public RunStatus Status { get; private set; } = RunStatus.Off;

    /// <summary>Where attempts are kept, or null.</summary>
    public RunHistoryStore? History => _history;

    /// <summary>Start an attempt when a save is loaded too (practice from a save), not only on a new game.</summary>
    public bool StartOnLoad { get; set; }

    /// <summary>
    /// An attempt from a save is timed on the game's own clock (the save's time included, so the splits match the game
    /// and the leaderboard's timing); false: from zero at the load.
    /// </summary>
    public bool SaveRunsOnGameClock { get; set; } = true;

    /// <summary>
    /// The split an attempt from a save starts at (the player's choice per split set; 0: the first). The splits before
    /// it stay empty — Lockpick never fills in a split it didn't see.
    /// </summary>
    public Func<SplitSet, int>? SaveStartSplit { get; set; }

    /// <summary>What to note on each new attempt about the checks it starts under (the leaderboard guard), or null.</summary>
    public Func<string?>? Checks { get; set; }

    /// <summary>
    /// Split on the triggers and the ending (the default). Off while another timer splits (LiveSplit's autosplitter,
    /// followed through its server): the split key then moves the run on, so a split is never counted twice.
    /// </summary>
    public bool AutoSplit { get; set; } = true;

    /// <summary>Raised when the status changes (any thread).</summary>
    public event EventHandler<RunStatus>? StatusChanged;

    /// <summary><see cref="RunStarted"/>, <see cref="SplitReached"/> and <see cref="RunEnded"/>, for the event log, captures and the API.</summary>
    public event EventHandler<GameEvent>? EventRaised;

    /// <summary>Raised when an attempt ends, with its record (already saved when there is a history store).</summary>
    public event EventHandler<RunRecord>? AttemptEnded;

    /// <summary>The adapter's run rules for the attached game, or null.</summary>
    public IRunRules? Rules => _source.Status.Profile is { } profile ? _rulesFor(profile) : null;

    /// <summary>Deaths since Lockpick started, in any play (not only timed runs).</summary>
    public int Deaths { get; private set; }

    /// <summary>The player died (any play; raised once per death, on the monitor thread).</summary>
    public event EventHandler? Died;

    /// <summary>Times runs against <paramref name="set"/> from the next new game (an attempt in progress is reset).</summary>
    public void Choose(SplitSet set)
    {
        List<GameEvent> raised = [];
        lock (_gate)
        {
            if (Status.State == RunState.Running)
            {
                End(RunResult.Reset, raised);
            }

            Status = RunStatus.Off with { State = RunState.Ready, Set = set, Attempt = Status.Set?.Id == set.Id ? Status.Attempt : 0 };
        }

        Publish(raised);
    }

    /// <summary>Turns runs off (an attempt in progress is reset).</summary>
    public void Stop()
    {
        List<GameEvent> raised = [];
        lock (_gate)
        {
            if (Status.State == RunState.Running)
            {
                End(RunResult.Reset, raised);
            }

            Status = RunStatus.Off;
        }

        Publish(raised);
    }

    /// <summary>The split key: ends the current split now.</summary>
    public void Split()
    {
        List<GameEvent> raised = [];
        lock (_gate)
        {
            if (Status.State == RunState.Running)
            {
                SplitNow(raised);
            }
        }

        Publish(raised);
    }

    /// <summary>Takes back the last split.</summary>
    public void Undo()
    {
        lock (_gate)
        {
            if (Status.State != RunState.Running || Status.Current == 0)
            {
                return;
            }

            var times = Status.Times.ToArray();
            times[Status.Current - 1] = null;
            Status = Status with { Current = Status.Current - 1, Times = times };
        }

        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Moves on without a time for the current split.</summary>
    public void Skip()
    {
        lock (_gate)
        {
            if (Status.State != RunState.Running || Status.Current >= Status.Times.Count - 1)
            {
                return;
            }

            Status = Status with { Current = Status.Current + 1 };
        }

        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Ends the attempt now (it is kept as reset) and waits for the next new game.</summary>
    public void Reset()
    {
        List<GameEvent> raised = [];
        lock (_gate)
        {
            if (Status.State == RunState.Running)
            {
                End(RunResult.Reset, raised);
            }
            else if (Status.State is RunState.Finished or RunState.Ended)
            {
                Status = Status with { State = RunState.Ready };
            }
        }

        Publish(raised);
    }

    public void Dispose()
    {
        // An attempt still going is kept, not lost.
        List<GameEvent> raised = [];
        lock (_gate)
        {
            if (Status.State == RunState.Running)
            {
                End(RunResult.LockpickClosed, raised);
            }
        }

        _source.Polled -= OnPolled;
        _source.EventRaised -= OnEvent;
        if (_trainer is not null)
        {
            _trainer.EventRaised -= OnTrainerEvent;
        }
    }

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        List<GameEvent> raised = [];
        var changed = false;
        var died = false;
        lock (_gate)
        {
            if (snapshot.GameTime.TryGet(out var time))
            {
                _gameTime = time;
            }

            if (Rules is { } any)
            {
                var dead = any.PlayerDied(snapshot);
                died = dead && _dead == false; // once per death: HP stays below zero until the game over screen
                _dead = dead;
                if (died)
                {
                    Deaths++;
                }
            }

            if (Status.State == RunState.Running && Rules is { } rules)
            {
                if (rules.PlayerDied(snapshot))
                {
                    End(RunResult.Died, raised);
                }
                else if (rules.AtTitle(snapshot))
                {
                    End(RunResult.QuitToTitle, raised);
                }
                else if (!AutoSplit)
                {
                    // Another timer splits; deaths and the title menu still end the attempt.
                }
                else if (_previous is { } previous && rules.RunFinished(previous, snapshot))
                {
                    // The ending: the final split, whichever split was current.
                    Status = Status with { Current = Status.Times.Count - 1 };
                    SplitNow(raised);
                }
                else
                {
                    _heldCheck = _heldCheckNext;
                    _heldCheckNext = false;
                    if (Triggered(Status.Set!.Splits[Status.Current].Trigger, snapshot))
                    {
                        SplitNow(raised);
                    }

                    _heldCheck = false;
                }

                changed = raised.Count > 0 | CheckCharacter(snapshot);
            }

            if (snapshot.Location.TryGet(out var room))
            {
                _room = room;
            }

            if (snapshot.Inventory.TryGet(out var inventory))
            {
                _inventory = inventory;
            }

            _previous = snapshot;
        }

        if (changed)
        {
            Publish(raised);
        }

        if (died)
        {
            Died?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Triggered(SplitTrigger trigger, GameSnapshot snapshot)
    {
        switch (trigger)
        {
            case RoomEntered entered when snapshot.Location.TryGet(out var room) && _room is { } before && room.Id != before.Id:
                return room.Id == entered.Room
                    && (entered.From is null || before.Id == entered.From)
                    && (!entered.FromOtherStage || before.Stage != room.Stage);
            case ItemObtained item when snapshot.Inventory.TryGet(out var held) && _inventory is { } previous:
                return (Count(held, item.ItemId) > Count(previous, item.ItemId) || (_heldCheck && Count(held, item.ItemId) > 0))
                    && (item.Room is null || (snapshot.Location.TryGet(out var here) && here.Id == item.Room));
            default:
                return false;
        }
    }

    private static int Count(IReadOnlyList<HeldItem> items, int itemId) => items.Where(i => i.ItemId == itemId).Sum(i => Math.Max(1, i.Quantity));

    private void OnEvent(object? sender, GameEvent gameEvent)
    {
        List<GameEvent> raised = [];
        var changed = false;
        lock (_gate)
        {
            if (gameEvent is GameStopped)
            {
                _dead = null; // the next game, or the same one attached again, starts unknown
            }

            switch (gameEvent)
            {
                case GameLoaded loaded when loaded.IsNewGame && _source.Latest is { } shown && Rules is { } r && !r.IsRealNewGame(shown):
                    break; // the game's demo (attract mode), not a run
                case GameLoaded loaded when Status.State != RunState.Off && (loaded.IsNewGame || (StartOnLoad && Status.State != RunState.Running)):
                    if (Status.State == RunState.Running)
                    {
                        End(RunResult.Reset, raised);
                    }

                    Begin(loaded, raised);
                    changed = true;
                    break;
                case PlayerDamaged damaged when Status.State == RunState.Running:
                    Status = Status with { DamageTaken = Status.DamageTaken + damaged.Amount };
                    changed = true;
                    break;
                case GameStopped when Status.State == RunState.Running:
                    End(RunResult.GameClosed, raised);
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            Publish(raised);
        }
    }

    private void OnTrainerEvent(object? sender, GameEvent gameEvent)
    {
        if (gameEvent is not RunInvalidated)
        {
            return;
        }

        lock (_gate)
        {
            if (Status.State != RunState.Running || Status.CheatsUsed)
            {
                return;
            }

            Status = Status with { CheatsUsed = true };
        }

        StatusChanged?.Invoke(this, Status);
    }

    private void Begin(GameLoaded loaded, List<GameEvent> raised)
    {
        var set = Status.Set!;
        var now = _time.GetUtcNow();
        Status = RunStatus.Off with
        {
            State = RunState.Running,
            Set = set,
            // Numbered across sessions: after the attempts already kept for these splits.
            Attempt = Math.Max(Status.Attempt, SavedAttempts(set)) + 1,
            Current = loaded.IsNewGame ? 0 : Math.Clamp(SaveStartSplit?.Invoke(set) ?? 0, 0, set.Splits.Count - 1),
            Times = new SplitTime?[set.Splits.Count],
            // A new game's clock is the run's (as on the leaderboard); from a save, the game's clock too, unless the
            // player times from the load.
            GameTimeOffset = loaded.IsNewGame || SaveRunsOnGameClock ? TimeSpan.Zero : loaded.GameTime,
            StartedAt = now,
            FromSave = !loaded.IsNewGame,
            CheatsUsed = _trainer?.Mode == TrainerMode.Trainer,
            Checks = Checks?.Invoke(),
        };
        _characterChecked = false;
        raised.Add(new RunStarted(now, set.Name, Status.Attempt, !loaded.IsNewGame));
    }

    /// <summary>
    /// Once per attempt, at its start: the splits are for the character being played. Checked only at the start, as
    /// Chris's game hands control to Rebecca later on.
    /// </summary>
    private bool CheckCharacter(GameSnapshot snapshot)
    {
        if (_characterChecked || snapshot.InGameplay is not { HasValue: true, Value: true } || !snapshot.Character.TryGet(out var playing)
            || playing is not (PlayableCharacter.Jill or PlayableCharacter.Chris))
        {
            return false;
        }

        _characterChecked = true;
        if (Status.Set is not { Character: PlayableCharacter.Jill or PlayableCharacter.Chris } set || set.Character == playing)
        {
            return false;
        }

        Status = Status with { Warning = $"Wrong splits? These are {set.Character}'s, but you're playing {playing}: choose {playing}'s splits on the Runs page." };
        return true;
    }

    /// <summary>The highest attempt number recorded for <paramref name="set"/> (0 when none): the next one is this + 1.</summary>
    public int SavedAttempts(SplitSet set)
    {
        try
        {
            return _history?.Load(set.Id) is { Count: > 0 } records ? records.Max(r => r.Attempt) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void SplitNow(List<GameEvent> raised)
    {
        var now = _time.GetUtcNow();
        var gameTime = (_gameTime ?? Status.GameTimeOffset) - Status.GameTimeOffset;
        var times = Status.Times.ToArray();
        var index = Status.Current;
        times[index] = new SplitTime(gameTime, now - Status.StartedAt!.Value);
        raised.Add(new SplitReached(now, Status.Set!.Splits[index].Name, index, gameTime));
        if (index == times.Length - 1)
        {
            Status = Status with { Times = times };
            End(RunResult.Finished, raised);
        }
        else
        {
            Status = Status with { Current = index + 1, Times = times };
            _heldCheckNext = true;
        }
    }

    private void End(RunResult result, List<GameEvent> raised)
    {
        var now = _time.GetUtcNow();
        var gameTime = (_gameTime ?? Status.GameTimeOffset) - Status.GameTimeOffset;
        if (result == RunResult.Finished && Status.Times[^1] is { } last)
        {
            gameTime = last.GameTime;
        }

        Status = Status with
        {
            State = result == RunResult.Finished ? RunState.Finished : RunState.Ended,
            Result = result,
            EndedAt = now,
            EndGameTime = gameTime,
        };
        raised.Add(new RunEnded(now, Status.Set!.Name, result, gameTime));
        var record = RunRecord.From(Status, _source.Status.Profile?.Id, _source.Status.Fingerprint?.Sha256);
        try
        {
            _history?.Add(record);
        }
        catch (IOException)
        {
            // The record stays in memory (AttemptEnded); a full or locked disk must not stop the run.
        }
        catch (UnauthorizedAccessException)
        {
        }

        _pendingRecord = record;
    }

    private void Publish(List<GameEvent> raised)
    {
        foreach (var e in raised)
        {
            EventRaised?.Invoke(this, e);
        }

        StatusChanged?.Invoke(this, Status);
        if (Interlocked.Exchange(ref _pendingRecord, null) is { } record)
        {
            AttemptEnded?.Invoke(this, record);
        }
    }
}
