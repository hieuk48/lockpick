using System.Threading.Channels;
using Lockpick.Core.Events;

namespace Lockpick.Api;

/// <summary>One event as the API sends it: a sequence number (to resume after a reconnect), its type and a description.</summary>
public sealed record ApiEvent(long Sequence, DateTimeOffset Timestamp, string Type, string Text, GameEvent Event);

/// <summary>The game's and the trainer's events, numbered, the latest kept for polling and pushed to stream listeners.</summary>
public sealed class EventFeed
{
    private readonly object _gate = new();
    private readonly LinkedList<ApiEvent> _recent = new();
    private readonly List<Channel<ApiEvent>> _listeners = [];
    private readonly int _keep;
    private long _sequence;

    public EventFeed(int keep = 500) => _keep = keep;

    public void Add(GameEvent gameEvent)
    {
        ApiEvent item;
        Channel<ApiEvent>[] listeners;
        lock (_gate)
        {
            item = new ApiEvent(++_sequence, gameEvent.Timestamp, gameEvent.GetType().Name, gameEvent.Describe(), gameEvent);
            _recent.AddLast(item);
            while (_recent.Count > _keep)
            {
                _recent.RemoveFirst();
            }

            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener.Writer.TryWrite(item); // bounded: a stalled listener drops its oldest events, never blocks the game
        }
    }

    /// <summary>Events after <paramref name="after"/>, oldest first.</summary>
    public IReadOnlyList<ApiEvent> Since(long after)
    {
        lock (_gate)
        {
            return [.. _recent.Where(e => e.Sequence > after)];
        }
    }

    public ChannelReader<ApiEvent> Listen(out Action stop)
    {
        var channel = Channel.CreateBounded<ApiEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _listeners.Add(channel);
        }

        stop = () =>
        {
            lock (_gate)
            {
                _listeners.Remove(channel);
            }

            channel.Writer.TryComplete();
        };
        return channel.Reader;
    }
}
