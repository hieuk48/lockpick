using System.Text.RegularExpressions;
using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.Core.Insight;

/// <summary>
/// What just happened in the game, in plain words (#43): items picked up, used, received or combined, documents and maps
/// taken, first visits, unlocks, kills, enemies appearing, damage, pushing and named story events — the adapter's
/// change explainer (the same knowledge as captures and the Progress page) run on every pair of polls. Raises
/// <see cref="Happened"/> events for the event log, the overlay and captures. Read-only.
/// </summary>
public sealed partial class EventFeed : IDisposable
{
    private const int MaxRecent = 30;

    private readonly IGameStateSource _source;
    private readonly Func<VersionProfile, IChangeExplainer?> _explainerFor;
    private readonly Func<string, string?> _roomName;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Happened> _recent = [];
    private VersionProfile? _profile;
    private IChangeExplainer? _explainer;
    private GameSnapshot? _previous;

    public EventFeed(IGameStateSource source, Func<VersionProfile, IChangeExplainer?> explainerFor, Func<string, string?> roomName, TimeProvider? time = null)
    {
        _source = source;
        _explainerFor = explainerFor;
        _roomName = roomName;
        _time = time ?? TimeProvider.System;
        _source.Polled += OnPolled;
    }

    /// <summary>Raised on the monitor thread for each thing that happened.</summary>
    public event EventHandler<Happened>? EventRaised;

    /// <summary>The latest things that happened, oldest first.</summary>
    public IReadOnlyList<Happened> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    public void Dispose() => _source.Polled -= OnPolled;

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        List<Happened> raised = [];
        lock (_gate)
        {
            if (_source.Status.Profile is { } profile && !ReferenceEquals(profile, _profile))
            {
                _profile = profile;
                _explainer = _explainerFor(profile);
            }

            if (_explainer is not null && _previous is { } previous)
            {
                var report = _explainer.Explain(previous, snapshot);
                var now = _time.GetUtcNow();
                var gameTime = snapshot.GameTime.TryGet(out var t) ? t : (TimeSpan?)null;
                foreach (var text in report.Known.Distinct().Select(Words).OfType<string>())
                {
                    var happened = new Happened(now, text, gameTime);
                    raised.Add(happened);
                    _recent.Add(happened);
                }

                if (_recent.Count > MaxRecent)
                {
                    _recent.RemoveRange(0, _recent.Count - MaxRecent);
                }
            }

            _previous = snapshot;
        }

        foreach (var happened in raised)
        {
            EventRaised?.Invoke(this, happened);
        }
    }

    /// <summary>The explainer's words for a player: room names for ids, a capital first letter; null to leave it out.</summary>
    private string? Words(string text)
    {
        if (StoryEnded().IsMatch(text) || text is "started pushing something" or "stopped pushing")
        {
            return null; // a state set earlier ends, or a push still under way / too short: told when it ends
        }

        text = RoomId().Replace(text, m => _roomName(m.Groups[1].Value) is { } name ? $"{name} ({m.Groups[1].Value})" : m.Value);
        return text.Length == 0 ? null : char.ToUpperInvariant(text[0]) + text[1..];
    }

    [GeneratedRegex(@"^story \S+ #\d+ ended$")]
    private static partial Regex StoryEnded();

    /// <summary>"room 105" (the adapter's room ids are a stage digit and two hex digits).</summary>
    [GeneratedRegex(@"\broom ([1-7][0-9A-F]{2})\b")]
    private static partial Regex RoomId();
}
