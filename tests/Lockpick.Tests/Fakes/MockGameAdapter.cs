using Lockpick.Core.Adapters;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.Tests.Fakes;

/// <summary>
/// Game-agnostic adapter whose session returns scripted snapshots. Used to test the monitor's
/// lifecycle handling independently of any real game's memory layout.
/// </summary>
public sealed class MockGameAdapter : IGameAdapter
{
    public const string ExeName = "MockGame.exe";

    public static readonly VersionProfile Profile = new()
    {
        Id = "mock-1",
        GameId = "mock",
        DisplayName = "Mock Game 1.0",
        Executable = new ExecutableSpec
        {
            FileName = ExeName,
            FileSize = 1234,
            Sha256 = new string('A', 64),
            Machine = PeMachine.X86,
            TimeDateStamp = 0x11111111,
            ImageBase = 0x400000,
            SizeOfImage = 0x10000,
        },
    };

    public string GameId => "mock";

    public string DisplayName => "Mock Game";

    public IReadOnlyCollection<string> ExecutableNames { get; } = [ExeName];

    public IReadOnlyList<VersionProfile> Profiles { get; } = [Profile];

    /// <summary>Snapshots returned in order by each session; the last one repeats.</summary>
    public Queue<Func<long, DateTimeOffset, GameSnapshot>> Script { get; } = new();

    public string? RejectAttachWith { get; set; }

    /// <summary>The save folder reported for any executable (null: none).</summary>
    public string? SavesFolder { get; set; }

    public string? SaveDirectory(string executablePath) => SavesFolder;

    /// <summary>The trainer features reported for any profile (null: none).</summary>
    public Lockpick.Core.Trainer.IGameTrainer? Trainer { get; set; }

    public Lockpick.Core.Trainer.IGameTrainer? CreateTrainer(VersionProfile profile) => Trainer;

    public int? SaveSlot(string fileName) =>
        fileName.StartsWith("savedat", StringComparison.OrdinalIgnoreCase) && fileName.Length > 7 && char.IsAsciiDigit(fileName[7])
            ? fileName[7] - '0'
            : null;

    public int AttachCount { get; private set; }

    public ProfileMatch Identify(ExecutableFingerprint fingerprint) => ProfileMatcher.Match(Profiles, fingerprint);

    public IGameSession Attach(IAttachedProcess process, VersionProfile profile)
    {
        AttachCount++;
        if (RejectAttachWith is not null)
        {
            throw new AttachRejectedException(RejectAttachWith);
        }

        return new Session(this, profile);
    }

    public static GameSnapshot InRoom(long seq, DateTimeOffset at, int stage, int room, int camera, int hp) => new()
    {
        Sequence = seq,
        CapturedAt = at,
        InGameplay = Field<bool>.Valid(true),
        Location = Field<RoomLocation>.Valid(new RoomLocation(stage, room)),
        Camera = Field<int>.Valid(camera),
        Health = Field<Health>.Valid(new Health(hp, 140)),
    };

    public static GameSnapshot Unreadable(long seq, DateTimeOffset at) => new()
    {
        Sequence = seq,
        CapturedAt = at,
        InGameplay = Field<bool>.Unavailable,
        Location = Field<RoomLocation>.Unavailable,
        Health = Field<Health>.Unavailable,
        GameTime = Field<TimeSpan>.Unavailable,
    };

    private sealed class Session(MockGameAdapter owner, VersionProfile profile) : IGameSession
    {
        private Func<long, DateTimeOffset, GameSnapshot>? _current;

        public VersionProfile Profile { get; } = profile;

        public bool CaptureRegions { get; set; }

        public GameSnapshot Read(long sequence, DateTimeOffset now)
        {
            if (owner.Script.TryDequeue(out var next))
            {
                _current = next;
            }

            return _current?.Invoke(sequence, now) ?? Unreadable(sequence, now);
        }
    }
}
