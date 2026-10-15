using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.Tests.Fakes;

/// <summary>A state source driven by the test: attach, poll and raise events by hand.</summary>
public sealed class FakeStateSource : IGameStateSource
{
    public event EventHandler<MonitorStatus>? StatusChanged;

    public event EventHandler<GameSnapshot>? SnapshotChanged;

    public event EventHandler<GameSnapshot>? Polled;

    public event EventHandler<GameEvent>? EventRaised;

    public MonitorStatus Status { get; private set; } = MonitorStatus.Stopped(DateTimeOffset.UtcNow);

    public bool RecordRegions { get; set; }

    public GameSnapshot? Latest { get; private set; }

    public MonitorStatistics Statistics => new(0, 0, 0, 0, 0, 0);

    public void Attach(int pid, VersionProfile profile)
    {
        Status = new MonitorStatus(ConnectionState.Attached, "attached", DateTimeOffset.UtcNow,
            new ProcessCandidate(pid, "Test", null, null), null, profile);
        StatusChanged?.Invoke(this, Status);
    }

    public void Detach()
    {
        Status = new MonitorStatus(ConnectionState.Searching, "searching", DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, Status);
    }

    public void Poll(GameSnapshot snapshot)
    {
        Latest = snapshot;
        Polled?.Invoke(this, snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public void Raise(GameEvent gameEvent) => EventRaised?.Invoke(this, gameEvent);

    public void Start()
    {
    }

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeWriteHost : IProcessWriteHost
{
    public FakeMemory Memory { get; } = new();

    public int Opened { get; private set; }

    public int? LastProcessId { get; private set; }

    public FakeWriter? LastWriter { get; private set; }

    public bool Refuse { get; set; }

    public IProcessWriter OpenForWrite(int processId)
    {
        if (Refuse)
        {
            throw new ProcessAccessException("refused");
        }

        Opened++;
        LastProcessId = processId;
        LastWriter = new FakeWriter(processId, Memory);
        return LastWriter;
    }
}

public sealed class FakeWriter(int processId, FakeMemory memory) : IProcessWriter
{
    public int ProcessId { get; } = processId;

    public bool Disposed { get; private set; }

    public bool IgnoreWrites { get; set; }

    public bool TryRead(ulong address, Span<byte> destination) => !Disposed && memory.TryRead(address, destination);

    public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        if (Disposed)
        {
            return false;
        }

        if (!IgnoreWrites)
        {
            memory.Write(address, source);
        }

        return true;
    }

    public void Dispose() => Disposed = true;
}
