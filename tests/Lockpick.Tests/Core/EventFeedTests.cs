using Lockpick.Core.Events;
using Lockpick.Core.Insight;
using Lockpick.Core.State;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.Core;

public sealed class EventFeedTests : IDisposable
{
    private readonly FakeStateSource _source = new();
    private readonly StubExplainer _explainer = new();
    private readonly EventFeed _feed;
    private readonly List<Happened> _events = [];

    public EventFeedTests()
    {
        _feed = new EventFeed(_source, _ => _explainer, id => id == "105" ? "Dining room" : null);
        _feed.EventRaised += (_, e) => _events.Add(e);
        _source.Attach(1, TrainerServiceTests.Profile);
    }

    public void Dispose() => _feed.Dispose();

    private static GameSnapshot At(double seconds) => new() { GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(seconds)) };

    [Fact]
    public void The_explainer_s_known_changes_become_plain_words()
    {
        _source.Poll(At(1));
        _explainer.Known = ["picked up Emblem", "entered room 105", "entered room 10F", "story B #12 ended", "picked up Emblem"];
        _source.Poll(At(2));

        Assert.Equal(["Picked up Emblem", "Entered Dining room (105)", "Entered room 10F"], _events.Select(e => e.Text));
        Assert.All(_events, e => Assert.Equal(TimeSpan.FromSeconds(2), e.GameTime));
        Assert.Equal(3, _feed.Recent.Count);
    }

    [Fact]
    public void Nothing_is_explained_before_a_second_poll()
    {
        _explainer.Known = ["picked up Emblem"];
        _source.Poll(At(1));
        Assert.Empty(_events);
    }

    private sealed class StubExplainer : IChangeExplainer
    {
        public IReadOnlyList<string> Known { get; set; } = [];

        public ChangeReport Explain(GameSnapshot previous, GameSnapshot current)
        {
            var report = new ChangeReport([], Known);
            Known = [];
            return report;
        }
    }
}
