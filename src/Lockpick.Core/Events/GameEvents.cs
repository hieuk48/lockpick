using System.Text.Json.Serialization;
using Lockpick.Core.State;

namespace Lockpick.Core.Events;

/// <summary>
/// Base type for typed game events. Events are only raised when they can be derived from
/// verified (<see cref="FieldStatus.Valid"/>) data on both sides of a change.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GameStarted), nameof(GameStarted))]
[JsonDerivedType(typeof(GameStopped), nameof(GameStopped))]
[JsonDerivedType(typeof(GameLoaded), nameof(GameLoaded))]
[JsonDerivedType(typeof(RoomChanged), nameof(RoomChanged))]
[JsonDerivedType(typeof(CameraChanged), nameof(CameraChanged))]
[JsonDerivedType(typeof(PlayerHealthChanged), nameof(PlayerHealthChanged))]
[JsonDerivedType(typeof(PlayerDamaged), nameof(PlayerDamaged))]
[JsonDerivedType(typeof(GameSaved), nameof(GameSaved))]
[JsonDerivedType(typeof(TrainerActionExecuted), nameof(TrainerActionExecuted))]
[JsonDerivedType(typeof(RunInvalidated), nameof(RunInvalidated))]
[JsonDerivedType(typeof(ChallengeStarted), nameof(ChallengeStarted))]
[JsonDerivedType(typeof(ChallengeFailed), nameof(ChallengeFailed))]
[JsonDerivedType(typeof(RunStarted), nameof(RunStarted))]
[JsonDerivedType(typeof(SplitReached), nameof(SplitReached))]
[JsonDerivedType(typeof(RunEnded), nameof(RunEnded))]
[JsonDerivedType(typeof(Happened), nameof(Happened))]
public abstract record GameEvent(DateTimeOffset Timestamp)
{
    public abstract string Describe();
}

/// <summary>Lockpick attached to a running, supported game process.</summary>
public sealed record GameStarted(DateTimeOffset Timestamp, string GameName, string ProfileId, int ProcessId) : GameEvent(Timestamp)
{
    public override string Describe() => $"Attached to {GameName} (profile {ProfileId}, PID {ProcessId})";
}

/// <summary>The attached game process exited or Lockpick detached from it.</summary>
public sealed record GameStopped(DateTimeOffset Timestamp, string Reason) : GameEvent(Timestamp)
{
    public override string Describe() => $"Detached: {Reason}";
}

/// <summary>A new game began or a save was loaded, detected from discontinuities in the in-game timer.</summary>
public sealed record GameLoaded(DateTimeOffset Timestamp, TimeSpan GameTime, bool IsNewGame) : GameEvent(Timestamp)
{
    public override string Describe() => IsNewGame
        ? "New game started"
        : $"Save loaded (in-game time {(int)GameTime.TotalHours:D2}:{GameTime.Minutes:D2}:{GameTime.Seconds:D2})";
}

public sealed record RoomChanged(DateTimeOffset Timestamp, RoomLocation From, RoomLocation To) : GameEvent(Timestamp)
{
    public override string Describe() => $"Room {From} → {To}";
}

/// <summary>Camera (cut) changed while the player stayed in the same room.</summary>
public sealed record CameraChanged(DateTimeOffset Timestamp, RoomLocation Room, int From, int To) : GameEvent(Timestamp)
{
    public override string Describe() => $"Camera {From} → {To} in room {Room}";
}

public sealed record PlayerHealthChanged(DateTimeOffset Timestamp, int From, int To) : GameEvent(Timestamp)
{
    public override string Describe() => $"HP {From} → {To}";
}

/// <summary>Health dropped while the player stayed in the same room.</summary>
public sealed record PlayerDamaged(DateTimeOffset Timestamp, int Amount, int Remaining) : GameEvent(Timestamp)
{
    public override string Describe() => $"Damaged for {Amount} (HP {Remaining})";
}

/// <summary>
/// The game wrote a save file. Saves leave no trace in game memory, so this comes from watching the game's save folder
/// (read-only: the files are never opened).
/// </summary>
public sealed record GameSaved(DateTimeOffset Timestamp, int Slot, TimeSpan? GameTime) : GameEvent(Timestamp)
{
    public override string Describe() => GameTime is { } t
        ? $"Game saved to slot {Slot} (in-game time {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2})"
        : $"Game saved to slot {Slot}";
}

/// <summary>The trainer changed the game (Trainer Mode only): what, from what to what.</summary>
public sealed record TrainerActionExecuted(DateTimeOffset Timestamp, string Description) : GameEvent(Timestamp)
{
    public override string Describe() => $"Trainer: {Description}";
}

/// <summary>
/// The first trainer change since this run started (a new game or a loaded save). A record of what Lockpick did, not
/// an anti-cheat claim: Lockpick can't see what other programs do.
/// </summary>
public sealed record RunInvalidated(DateTimeOffset Timestamp, string Reason) : GameEvent(Timestamp)
{
    public override string Describe() => $"Run modified by the trainer: {Reason}";
}

/// <summary>A challenge attempt began (the player chose it, retried, or started a new game or loaded a save).</summary>
public sealed record ChallengeStarted(DateTimeOffset Timestamp, string Challenge, int Attempt) : GameEvent(Timestamp)
{
    public override string Describe() => $"Challenge: {Challenge}, attempt {Attempt}";
}

/// <summary>A challenge rule was broken; the attempt is over. Overlays and other tools show this.</summary>
public sealed record ChallengeFailed(DateTimeOffset Timestamp, string Challenge, string Reason, TimeSpan? GameTime) : GameEvent(Timestamp)
{
    public override string Describe() => GameTime is { } t
        ? $"Challenge failed ({Challenge}): {Reason} at {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"Challenge failed ({Challenge}): {Reason}";
}

/// <summary>A run attempt began (a new game, or a loaded save when runs start on loads).</summary>
public sealed record RunStarted(DateTimeOffset Timestamp, string SplitSet, int Attempt, bool FromSave) : GameEvent(Timestamp)
{
    public override string Describe() => $"Run: {SplitSet}, attempt {Attempt}{(FromSave ? " (from a save)" : string.Empty)}";
}

/// <summary>A run reached a split (its trigger fired, or the split key was pressed).</summary>
public sealed record SplitReached(DateTimeOffset Timestamp, string Split, int Index, TimeSpan GameTime) : GameEvent(Timestamp)
{
    public override string Describe() => $"Split {Index + 1}: {Split} at {Clock(GameTime)}";

    internal static string Clock(TimeSpan t) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}");
}

/// <summary>A run attempt ended: finished, or a death, the title menu, the game closing or a reset.</summary>
public sealed record RunEnded(DateTimeOffset Timestamp, string SplitSet, Runs.RunResult Result, TimeSpan GameTime) : GameEvent(Timestamp)
{
    public override string Describe() => Result switch
    {
        Runs.RunResult.Finished => $"Run finished: {SplitSet} in {SplitReached.Clock(GameTime)}",
        Runs.RunResult.Died => $"Run over: died at {SplitReached.Clock(GameTime)}",
        Runs.RunResult.QuitToTitle => $"Run over: back at the title at {SplitReached.Clock(GameTime)}",
        Runs.RunResult.GameClosed => "Run over: the game closed",
        _ => $"Run reset at {SplitReached.Clock(GameTime)}",
    };
}

/// <summary>Something that happened in the game, in plain words (the event feed): "Picked up Emblem", "Yawn appears".</summary>
public sealed record Happened(DateTimeOffset Timestamp, string Text, TimeSpan? GameTime) : GameEvent(Timestamp)
{
    public override string Describe() => Text;
}
