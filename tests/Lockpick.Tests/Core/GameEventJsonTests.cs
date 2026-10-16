using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockpick.Core.Events;

namespace Lockpick.Tests.Core;

public sealed class GameEventJsonTests
{
    /// <summary>
    /// Every event is written as JSON (the API's event list and stream, captures): an event type missing from
    /// GameEvent's [JsonDerivedType] list can't be written at all, so each must be registered.
    /// </summary>
    [Fact]
    public void Every_event_type_is_registered_for_json()
    {
        var registered = typeof(GameEvent).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType).ToHashSet();
        var events = typeof(GameEvent).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(GameEvent)) && !t.IsAbstract).ToList();
        Assert.NotEmpty(events);
        Assert.All(events, t => Assert.Contains(t, registered));
    }

    [Fact]
    public void Run_and_feed_events_round_trip()
    {
        var at = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        GameEvent[] events =
        [
            new RunStarted(at, "Jill - Bad Ending - NMG", 2, false),
            new SplitReached(at, "King Henry", 0, TimeSpan.FromMinutes(3)),
            new RunEnded(at, "Jill - Bad Ending - NMG", Lockpick.Core.Runs.RunResult.Died, TimeSpan.FromMinutes(9)),
            new Happened(at, "Picked up Emblem", TimeSpan.FromSeconds(75)),
        ];
        foreach (var e in events)
        {
            var json = JsonSerializer.Serialize<GameEvent>(e);
            Assert.Equal(e, JsonSerializer.Deserialize<GameEvent>(json));
        }
    }
}
