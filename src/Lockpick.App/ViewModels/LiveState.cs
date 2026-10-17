using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

public sealed record EventItem(DateTimeOffset Timestamp, string Kind, string Text, bool IsWarning)
{
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// UI-thread view of an <see cref="IGameStateSource"/>. Background notifications are queued and applied
/// in a single dispatcher pass; snapshots are coalesced so the UI only renders the newest one.
/// </summary>
public sealed partial class LiveState : ObservableObject
{
    public const int MaxEvents = 300;

    private readonly ConcurrentQueue<GameEvent> _pendingEvents = new();
    private GameSnapshot? _pendingSnapshot;
    private MonitorStatus? _pendingStatus;
    private int _flushScheduled;

    public LiveState(IGameStateSource source)
    {
        Source = source;
        Status = source.Status;
        source.StatusChanged += (_, s) =>
        {
            Volatile.Write(ref _pendingStatus, s);
            ScheduleFlush();
        };
        source.SnapshotChanged += (_, s) =>
        {
            Volatile.Write(ref _pendingSnapshot, s);
            ScheduleFlush();
        };
        source.EventRaised += (_, e) =>
        {
            // The event log reads like the feed: camera cuts are left out, and rooms and damage come from the feed's
            // plain words ("Entered Dining room (105)", "Took damage (96 → 90, Zombie bite (6))") instead of the raw
            // "Room 106 → 105" / "HP 96 → 86" / "Damaged for 10". Captures still record every raw event.
            if (e is CameraChanged or RoomChanged or PlayerHealthChanged or PlayerDamaged)
            {
                return;
            }

            _pendingEvents.Enqueue(e);
            ScheduleFlush();
        };
    }

    public IGameStateSource Source { get; }

    /// <summary>Adds an event from elsewhere (the trainer) to the event log, in order with the game's own.</summary>
    public void Add(GameEvent gameEvent)
    {
        _pendingEvents.Enqueue(gameEvent);
        ScheduleFlush();
    }

    [ObservableProperty]
    public partial MonitorStatus Status { get; private set; }

    [ObservableProperty]
    public partial GameSnapshot? Snapshot { get; private set; }

    /// <summary>Newest first.</summary>
    public ObservableCollection<EventItem> Events { get; } = [];

    /// <summary>Applies pending updates immediately (used by tests and the dispatcher callback).</summary>
    public void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        if (Interlocked.Exchange(ref _pendingStatus, null) is { } status)
        {
            Status = status;
            if (status.State is ConnectionState.Searching or ConnectionState.Unsupported or ConnectionState.Stopped)
            {
                Snapshot = null;
            }
        }

        if (Interlocked.Exchange(ref _pendingSnapshot, null) is { } snapshot && Status.State is ConnectionState.Attached or ConnectionState.Replaying)
        {
            Snapshot = snapshot;
        }

        while (_pendingEvents.TryDequeue(out var e))
        {
            Events.Insert(0, new EventItem(e.Timestamp, e.GetType().Name, e.Describe(), e is PlayerDamaged or GameStopped or RunInvalidated));
            if (Events.Count > MaxEvents)
            {
                Events.RemoveAt(Events.Count - 1);
            }
        }
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }
}
