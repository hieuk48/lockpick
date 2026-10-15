using Lockpick.Core.Events;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ValueType = Lockpick.Core.Memory.ValueType;

namespace Lockpick.Tests.Core;

public sealed class TrainerServiceTests : IDisposable
{
    private const ulong Hp = 0x401000;
    private const ulong MaxHp = 0x401002;
    private const ulong Room = 0x401004;

    private static readonly VersionProfile Profile = new()
    {
        Id = "test",
        GameId = "test",
        DisplayName = "Test",
        Executable = new ExecutableSpec { FileName = "Test.exe", Sha256 = new string('0', 64), ImageBase = 0x400000, SizeOfImage = 0x10000 },
        Fields =
        [
            new FieldSpec { Name = "hp", Address = Hp, Type = ValueType.I16, Verification = Verification.Runtime, Evidence = "test",
                Write = new WriteSpec { Min = 1, Max = 999, MaxField = "maxHp" } },
            new FieldSpec { Name = "maxHp", Address = MaxHp, Type = ValueType.U8, Verification = Verification.Runtime, Evidence = "test" },
            new FieldSpec { Name = "room", Address = Room, Type = ValueType.U8, Verification = Verification.Runtime, Evidence = "test" },
            new FieldSpec { Name = "lead", Address = Room + 1, Type = ValueType.U8, Verification = Verification.Unverified,
                Write = new WriteSpec { Min = 0, Max = 9 } },
        ],
    };

    private readonly FakeSource _source = new();
    private readonly FakeWriteHost _host = new();
    private readonly TrainerActionLog _log = new(null);
    private readonly TrainerService _trainer;

    public TrainerServiceTests()
    {
        _host.Memory.WriteI16(Hp, 50).WriteU8(MaxHp, 96).WriteU8(Room, 5);
        _trainer = new TrainerService(_source, _host, _log, NullLogger.Instance);
        _source.Attach(1234);
        _source.Poll(InGame(hp: 50));
    }

    public void Dispose()
    {
        _trainer.Dispose();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static GameSnapshot InGame(long hp, bool inGame = true) => new()
    {
        InGameplay = Field<bool>.Valid(inGame),
        RawFields = new Dictionary<string, Field<long>> { ["hp"] = Field<long>.Valid(hp), ["maxHp"] = Field<long>.Valid(96) },
    };

    private short HpNow()
    {
        Span<byte> bytes = stackalloc byte[2];
        Assert.True(_host.Memory.TryRead(Hp, bytes));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes);
    }

    [Fact]
    public void Read_only_by_default_with_no_write_handle()
    {
        Assert.Equal(TrainerMode.ReadOnly, _trainer.Mode);
        Assert.False(_trainer.HasWriteAccess);
        Assert.Equal(0, _host.Opened);

        var action = _trainer.Write("hp", 96);
        Assert.Equal(TrainerResult.Refused, action.Result);
        Assert.Equal(50, HpNow());
    }

    [Fact]
    public void Trainer_mode_opens_a_write_handle_and_writes_are_read_back_and_logged()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.True(_trainer.HasWriteAccess);
        Assert.Equal(1234, _host.LastProcessId);

        var action = _trainer.Write("hp", 96);
        Assert.Equal(TrainerResult.Applied, action.Result);
        Assert.Equal(50, action.OldValue);
        Assert.Equal(96, HpNow());
        Assert.Contains(_log.Recent, a => a.Field == "hp" && a.NewValue == 96 && a.Result == TrainerResult.Applied);
    }

    [Fact]
    public void Writes_outside_the_bounds_or_to_unwritable_fields_are_refused()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.Equal(TrainerResult.Refused, _trainer.Write("hp", 97).Result);   // above max HP (96, read live)
        Assert.Equal(TrainerResult.Refused, _trainer.Write("hp", 0).Result);    // below the minimum
        Assert.Equal(TrainerResult.Refused, _trainer.Write("room", 3).Result);  // no write bounds declared
        Assert.Equal(TrainerResult.Refused, _trainer.Write("lead", 3).Result);  // not runtime-verified
        Assert.Equal(TrainerResult.Refused, _trainer.Write("nope", 3).Result);
        Assert.Equal(50, HpNow());
    }

    [Fact]
    public void Nothing_is_written_outside_a_game()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        _source.Poll(InGame(50, inGame: false));
        Assert.Equal(TrainerResult.Refused, _trainer.Write("hp", 96).Result);
    }

    [Fact]
    public void Challenge_mode_never_opens_a_handle_and_refuses_everything()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        _trainer.Lock("hp", 90);
        _trainer.SetMode(TrainerMode.Challenge);

        Assert.False(_trainer.HasWriteAccess);
        Assert.True(_host.LastWriter!.Disposed);
        Assert.Empty(_trainer.Locks);
        var action = _trainer.Write("hp", 96);
        Assert.Equal(TrainerResult.Refused, action.Result);
        Assert.Contains("Challenge Mode", action.Message, StringComparison.Ordinal);

        _source.Detach();
        _source.Attach(5678);
        Assert.Equal(1, _host.Opened); // reattaching in Challenge Mode opens nothing
    }

    [Fact]
    public void A_lock_is_reapplied_when_the_game_changes_the_value()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.Equal(TrainerResult.Applied, _trainer.Lock("hp", 90).Result);

        _host.Memory.WriteI16(Hp, 80);       // a bite
        _source.Poll(InGame(hp: 80));
        Assert.Equal(90, HpNow());
        Assert.Contains(_log.Recent, a => a.Source == TrainerSource.Lock && a.OldValue == 80 && a.NewValue == 90);

        _trainer.Unlock("hp");
        _host.Memory.WriteI16(Hp, 70);
        _source.Poll(InGame(hp: 70));
        Assert.Equal(70, HpNow());
    }

    [Fact]
    public void A_rule_writes_what_it_asks_for_on_each_poll()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.Equal(TrainerResult.Applied, _trainer.SetRule("heal", "Top up", s => s.RawFields["hp"].Value < 60 ? [new FieldWrite("hp", 60)] : []).Result);
        _source.Poll(InGame(hp: 50));
        Assert.Equal(60, HpNow());

        _trainer.ClearRule("heal");
        _host.Memory.WriteI16(Hp, 40);
        _source.Poll(InGame(hp: 40));
        Assert.Equal(40, HpNow());
    }

    [Fact]
    public void The_first_change_in_a_run_invalidates_it_until_the_next_game_or_load()
    {
        var events = new List<GameEvent>();
        _trainer.EventRaised += (_, e) => events.Add(e);
        _trainer.SetMode(TrainerMode.Trainer);

        _trainer.Write("hp", 96);
        _trainer.Write("hp", 80);
        Assert.True(_trainer.RunModified);
        Assert.Single(events.OfType<RunInvalidated>());
        Assert.Equal(2, events.OfType<TrainerActionExecuted>().Count());

        _source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), false));
        Assert.False(_trainer.RunModified);
        _trainer.Write("hp", 70);
        Assert.Equal(2, events.OfType<RunInvalidated>().Count());
    }

    [Fact]
    public void Detaching_closes_the_handle_and_reattaching_in_trainer_mode_opens_a_new_one()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        var first = _host.LastWriter!;
        _source.Detach();
        Assert.True(first.Disposed);
        Assert.False(_trainer.HasWriteAccess);

        _source.Attach(4321);
        Assert.True(_trainer.HasWriteAccess);
        Assert.Equal(4321, _host.LastProcessId);
    }

    [Fact]
    public void A_refused_handle_is_reported_and_nothing_is_written()
    {
        _host.Refuse = true;
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.False(_trainer.HasWriteAccess);
        Assert.NotNull(_trainer.WriteAccessError);
        Assert.Equal(TrainerResult.Refused, _trainer.Write("hp", 96).Result);
    }

    [Fact]
    public void A_write_that_does_not_stick_is_reported_as_a_mismatch()
    {
        _trainer.SetMode(TrainerMode.Trainer);
        _host.LastWriter!.IgnoreWrites = true;
        Assert.Equal(TrainerResult.Mismatch, _trainer.Write("hp", 96).Result);
    }

    [Fact]
    public void Profiles_reject_write_bounds_on_unverified_or_masked_fields()
    {
        var bad = Profile with
        {
            Fields = [new FieldSpec { Name = "x", Address = Room, Type = ValueType.U8, Verification = Verification.Static, Evidence = "e", Write = new WriteSpec { Max = 1 } }],
        };
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Validate(bad));

        var outOfType = Profile with
        {
            Fields = [new FieldSpec { Name = "x", Address = Room, Type = ValueType.U8, Verification = Verification.Runtime, Evidence = "e", Write = new WriteSpec { Max = 300 } }],
        };
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Validate(outOfType));
    }

    private sealed class FakeSource : IGameStateSource
    {
        public event EventHandler<MonitorStatus>? StatusChanged;

        public event EventHandler<GameSnapshot>? SnapshotChanged;

        public event EventHandler<GameSnapshot>? Polled;

        public event EventHandler<GameEvent>? EventRaised;

        public MonitorStatus Status { get; private set; } = MonitorStatus.Stopped(DateTimeOffset.UtcNow);

        public bool RecordRegions { get; set; }

        public GameSnapshot? Latest { get; private set; }

        public MonitorStatistics Statistics => new(0, 0, 0, 0, 0, 0);

        public void Attach(int pid)
        {
            Status = new MonitorStatus(ConnectionState.Attached, "attached", DateTimeOffset.UtcNow,
                new ProcessCandidate(pid, "Test", null, null), null, Profile);
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

    private sealed class FakeWriteHost : IProcessWriteHost
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

    private sealed class FakeWriter(int processId, FakeMemory memory) : IProcessWriter
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
}
