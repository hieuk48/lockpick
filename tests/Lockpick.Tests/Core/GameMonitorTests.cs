using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.State;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.Core;

/// <summary>Process lifecycle and failure handling, driven deterministically through <see cref="GameMonitor.Tick"/>.</summary>
public sealed class GameMonitorTests : IAsyncDisposable
{
    private const string ExePath = @"C:\Games\MockGame.exe";

    private readonly MockGameAdapter _adapter = new();
    private readonly FakeProcessHost _host = new();
    private readonly FakeFingerprintProvider _fingerprints = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
    private readonly List<GameEvent> _events = [];
    private readonly List<MonitorStatus> _statuses = [];
    private readonly GameMonitor _monitor;

    public GameMonitorTests()
    {
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(MockGameAdapter.Profile, ExePath);
        _monitor = new GameMonitor(_adapter, _host, _fingerprints, new MonitorOptions { MaxConsecutiveFailedPolls = 3 }, time: _time);
        _monitor.EventRaised += (_, e) => _events.Add(e);
        _monitor.StatusChanged += (_, s) => _statuses.Add(s);
    }

    public ValueTask DisposeAsync() => _monitor.DisposeAsync();

    private FakeAttachedProcess Launch(int pid = 100) =>
        _host.Launch(pid, ExePath, new FakeMemory(), new ModuleImage(0x400000, 0x10000, ExePath));

    private void Scan()
    {
        _time.Advance(TimeSpan.FromSeconds(2));
        _monitor.Tick();
    }

    [Fact]
    public void Stays_searching_when_game_is_not_running()
    {
        Scan();
        Assert.Equal(ConnectionState.Searching, _monitor.Status.State);
        Assert.False(_monitor.IsAttached);
        Assert.Empty(_events);
    }

    [Fact]
    public void Attaches_to_supported_build_and_publishes_state()
    {
        Launch();
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140));

        Scan();

        Assert.True(_monitor.IsAttached);
        Assert.Equal(ConnectionState.Attached, _monitor.Status.State);
        Assert.Equal("mock-1", _monitor.Status.Profile!.Id);
        Assert.IsType<GameStarted>(Assert.Single(_events));
        Assert.Equal("106", _monitor.Latest!.Location.Value.Id);
    }

    [Fact]
    public void A_save_file_written_by_the_game_becomes_one_GameSaved_event()
    {
        var folder = Directory.CreateTempSubdirectory("lockpick-saves-").FullName;
        try
        {
            _adapter.SavesFolder = folder;
            Launch();
            _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140));
            Scan();

            // The game writes a save in several steps; other files in the folder are ignored.
            File.WriteAllBytes(Path.Combine(folder, "savedat3.dat"), [1, 2, 3]);
            File.AppendAllText(Path.Combine(folder, "savedat3.dat"), "more");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_events.OfType<GameSaved>().Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
                _monitor.Tick();
            }

            Thread.Sleep(300);
            _monitor.Tick();
            var saved = Assert.Single(_events.OfType<GameSaved>());
            Assert.Equal(3, saved.Slot);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Unknown_build_is_rejected_without_opening_the_process()
    {
        Launch();
        _fingerprints.ByPath[ExePath] = _fingerprints.ByPath[ExePath] with { Sha256 = new string('9', 64) };

        Scan();

        Assert.Equal(ConnectionState.Unsupported, _monitor.Status.State);
        Assert.Equal(0, _host.OpenCount);
        Assert.False(_monitor.IsAttached);
    }

    [Fact]
    public void Verification_failure_is_reported_and_process_released()
    {
        var process = Launch();
        _adapter.RejectAttachWith = "anchor mismatch";

        Scan();

        Assert.Equal(ConnectionState.Unsupported, _monitor.Status.State);
        Assert.Contains("anchor mismatch", _monitor.Status.Message, StringComparison.Ordinal);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void Access_denied_is_reported_as_unsupported_not_a_crash()
    {
        Launch();
        _host.OpenFailure = new ProcessAccessException("Access denied");
        Scan();
        Assert.Equal(ConnectionState.Unsupported, _monitor.Status.State);
    }

    [Fact]
    public void Game_exit_detaches_and_a_restart_is_reattached()
    {
        var first = Launch(100);
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140));
        Scan();

        _host.Exit(100);
        _monitor.Tick();

        Assert.False(_monitor.IsAttached);
        Assert.True(first.Disposed);
        Assert.Contains(_events, e => e is GameStopped { Reason: "Game process exited" });
        Assert.Equal(ConnectionState.Searching, _monitor.Status.State);

        Launch(200);
        Scan();
        Assert.True(_monitor.IsAttached);
        Assert.Equal(200, _monitor.Status.Process!.ProcessId);
        Assert.Equal(2, _events.Count(e => e is GameStarted));
    }

    [Fact]
    public void Repeated_read_failures_drop_the_session()
    {
        Launch();
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.Unreadable(s, t));
        Scan(); // attach + first failed poll
        _monitor.Tick();
        Assert.True(_monitor.IsAttached);
        _monitor.Tick(); // third consecutive failure

        Assert.False(_monitor.IsAttached);
        Assert.Contains(_events, e => e is GameStopped stop && stop.Reason.Contains("failed", StringComparison.Ordinal));
        Assert.Equal(3, _monitor.Statistics.FailedPolls);
    }

    [Fact]
    public void State_changes_raise_snapshot_and_typed_events()
    {
        Launch();
        var snapshots = new List<GameSnapshot>();
        _monitor.SnapshotChanged += (_, s) => snapshots.Add(s);
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140));
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140)); // unchanged
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 2, 140));
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 5, 0, 120));

        Scan();
        _monitor.Tick();
        _monitor.Tick();
        _monitor.Tick();

        Assert.Equal(3, snapshots.Count); // the unchanged poll is not re-published
        Assert.Contains(_events, e => e is CameraChanged { From: 0, To: 2 });
        Assert.Contains(_events, e => e is RoomChanged);
        Assert.DoesNotContain(_events, e => e is PlayerDamaged); // HP drop coincided with a room change
    }

    [Fact]
    public void Status_is_only_raised_when_the_situation_changes()
    {
        Scan();
        Scan();
        Scan();
        Assert.Single(_statuses);
    }

    [Fact]
    public async Task Background_loop_starts_and_stops_cleanly()
    {
        Launch();
        _adapter.Script.Enqueue((s, t) => MockGameAdapter.InRoom(s, t, 1, 6, 0, 140));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, new MonitorOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        var attached = new TaskCompletionSource();
        monitor.StatusChanged += (_, s) =>
        {
            if (s.State == ConnectionState.Attached)
            {
                attached.TrySetResult();
            }
        };

        monitor.Start();
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await monitor.StopAsync();

        Assert.Equal(ConnectionState.Stopped, monitor.Status.State);
        var stats = monitor.Statistics;
        Assert.True(stats.Polls > 1);

        // The rate must not decay after polling stops (it is measured between first and last poll).
        await Task.Delay(200);
        Assert.Equal(stats.MeasuredPollHz, monitor.Statistics.MeasuredPollHz, 3);
    }
}
