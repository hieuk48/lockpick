using Lockpick.Core.State;

namespace Lockpick.Core.Insight;

/// <summary>A change in the game's state that the adapter has no explanation for (e.g. an unknown story flag).</summary>
/// <param name="Key">Stable identity, so the same change is asked about only once.</param>
/// <param name="Description">What changed, in words ("story flag B0 #7 set").</param>
/// <param name="ByAction">
/// A story/event change that a player action at the same moment can account for (placing a crest, being handed an
/// item, an enemy appearing); <see cref="QuestionScheduler"/> attributes it to such an action instead of asking.
/// </param>
public sealed record UnexplainedChange(string Key, string Description, bool ByAction = false);

/// <summary>What changed between two polls: changes the game's data can't explain, and notable ones it can.</summary>
/// <param name="Known">Explained events worth showing as context ("picked up Emblem", "entered room 105").</param>
public sealed record ChangeReport(IReadOnlyList<UnexplainedChange> Unexplained, IReadOnlyList<string> Known)
{
    public static readonly ChangeReport Empty = new([], []);

    /// <summary>
    /// Player actions in this poll that can account for story changes around them ("used Star crest", "received Radio",
    /// "Yawn appears"); also listed in <see cref="Known"/>.
    /// </summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>Explains the changes between two consecutive snapshots using the game's known data.</summary>
public interface IChangeExplainer
{
    ChangeReport Explain(GameSnapshot previous, GameSnapshot current);
}

/// <summary>
/// A group of unexplained changes to ask the player about, with where and when the first one happened and the known
/// events around it (so the player can tell which moment is meant).
/// </summary>
public sealed record PendingQuestion(IReadOnlyList<UnexplainedChange> Changes, string? Room, TimeSpan? GameTime, DateTimeOffset At)
{
    /// <summary>Known events shortly before and after the first change, oldest first, e.g. "1:40 picked up Emblem".</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>A story change put down to a player action at the same moment instead of being asked about.</summary>
public sealed record AttributedChange(UnexplainedChange Change, string Action, string? Room, TimeSpan? GameTime, DateTimeOffset At);

/// <summary>
/// Collects unexplained changes and decides when to ask about them. Story events set their flags as a cutscene starts,
/// so the question waits until the changes have stopped for <see cref="QuietPeriod"/> and the game is playing again
/// (in a game session with the in-game timer running). A story change within <see cref="ActionWindow"/> of a player
/// action is attributed to it instead of asked about. Each change is considered once per capture.
/// </summary>
public sealed class QuestionScheduler(IChangeExplainer explainer)
{
    private const int MaxChangesPerQuestion = 12;
    private const int MaxRecent = 12;
    private static readonly TimeSpan ContextBefore = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<(UnexplainedChange Change, DateTimeOffset At, GameSnapshot Snapshot)> _pending = [];
    private readonly List<(DateTimeOffset At, string Text)> _recent = [];
    private readonly List<(DateTimeOffset At, string Text)> _actions = [];
    private GameSnapshot? _previous;
    private DateTimeOffset _lastChangeAt;

    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How close (either side) a player action must be to account for a story change.</summary>
    public TimeSpan ActionWindow { get; init; } = TimeSpan.FromSeconds(5);

    private readonly List<AttributedChange> _attributed = [];

    /// <summary>Returns the story changes attributed to player actions since the last call (to record them).</summary>
    public IReadOnlyList<AttributedChange> TakeAttributed()
    {
        var taken = _attributed.ToList();
        _attributed.Clear();
        return taken;
    }

    /// <summary>Feeds the next poll; returns a question when one is ready to be asked.</summary>
    public PendingQuestion? Feed(GameSnapshot current)
    {
        var previous = _previous;
        _previous = current;
        if (previous is null)
        {
            return null;
        }

        var report = explainer.Explain(previous, current);
        foreach (var known in report.Known)
        {
            Remember(_recent, current.CapturedAt, $"{Clock(current)}{known}");
        }

        foreach (var action in report.Actions)
        {
            Remember(_actions, current.CapturedAt, action);
        }

        foreach (var change in report.Unexplained)
        {
            if (_seen.Add(change.Key) && _pending.Count < MaxChangesPerQuestion)
            {
                _pending.Add((change, current.CapturedAt, current));
                _lastChangeAt = current.CapturedAt;
            }
        }

        var playing = current.InGameplay is { HasValue: true, Value: true }
            && current.GameTime.TryGet(out var now) && previous.GameTime.TryGet(out var before) && now > before;
        if (_pending.Count == 0 || !playing || current.CapturedAt - _lastChangeAt < QuietPeriod)
        {
            return null;
        }

        // Story changes next to a player action are that action's doing.
        var ask = new List<(UnexplainedChange Change, DateTimeOffset At, GameSnapshot Snapshot)>();
        foreach (var pending in _pending)
        {
            var action = pending.Change.ByAction
                ? _actions.Find(a => (a.At - pending.At).Duration() <= ActionWindow).Text
                : null;
            if (action is null)
            {
                ask.Add(pending);
            }
            else
            {
                _attributed.Add(new AttributedChange(
                    pending.Change,
                    action,
                    pending.Snapshot.Location.TryGet(out var where) ? where.Id : null,
                    pending.Snapshot.GameTime.TryGet(out var when) ? when : null,
                    pending.At));
                Remember(_recent, pending.At, $"{Clock(pending.Snapshot)}{action} (with {pending.Change.Description})");
            }
        }

        _pending.Clear();
        if (ask.Count == 0)
        {
            return null;
        }

        var first = ask[0].Snapshot;
        return new PendingQuestion(
            [.. ask.Select(p => p.Change)],
            first.Location.TryGet(out var room) ? room.Id : null,
            first.GameTime.TryGet(out var time) ? time : null,
            first.CapturedAt)
        {
            Context = [.. _recent.Where(r => r.At >= first.CapturedAt - ContextBefore).OrderBy(r => r.At).Select(r => r.Text)],
        };
    }

    private static void Remember(List<(DateTimeOffset At, string Text)> list, DateTimeOffset at, string text)
    {
        list.Add((at, text));
        if (list.Count > MaxRecent)
        {
            list.RemoveAt(0);
        }
    }

    private static string Clock(GameSnapshot snapshot) =>
        snapshot.GameTime.TryGet(out var t) ? $"{(int)t.TotalMinutes}:{t.Seconds:00} " : string.Empty;
}
