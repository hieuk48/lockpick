using Lockpick.Core.Adapters;
using Lockpick.Core.Memory;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Reads RE1 profile fields and maps them onto the normalized <see cref="GameSnapshot"/>.</summary>
internal sealed class Re1Session : IGameSession
{
    internal static class Fields
    {
        public const string Stage = "stage";
        public const string Room = "room";
        public const string Camera = "camera";
        public const string Hp = "hp";
        public const string MaxHp = "maxHp";
        public const string Character = "character";
        public const string GameTimer = "gameTimer";
        public const string StateFlags = "stateFlags";
        public const string MainMenu = "mainMenu";
        public const string PositionX = "positionX";
        public const string PositionY = "positionY";
        public const string PositionZ = "positionZ";
    }

    internal const int FramesPerSecond = 30;
    internal const int StageCount = 7;
    internal const int MaxRoom = 0x1F;

    private readonly IProcessMemory _memory;
    private readonly IReadOnlyList<FieldSpec> _fields;
    private readonly HashSet<string> _trusted;
    private readonly Dictionary<string, Field<long>> _lastRaw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Cluster[] _clusters;
    private GameSnapshot? _previous;
    private Field<IReadOnlyList<PlacedItem>> _placed = Field<IReadOnlyList<PlacedItem>>.Unavailable;
    private RoomLocation? _placedRoom;
    private DateTimeOffset _placedAt;

    // The room action table (static: decomp g_RoomActionTable; live 2026-10-14: the pickups it lists match the
    // room files and the pickups made). 24 entries of 12 bytes; [0] handler (4 = item pickup), +8 the record
    // (zone x/z/width/depth as i16/i16/u16/u16, item id at +8). The tail pointer marks the last entry in use.
    internal const ulong ActionTable = 0xD91AA0;
    internal const ulong ActionTail = 0xD91BC0;
    private const int ActionEntries = 24;
    private const int PickupHandler = 4;
    private static readonly TimeSpan PlacedRefresh = TimeSpan.FromSeconds(1);

    public Re1Session(IProcessMemory memory, VersionProfile profile)
    {
        _memory = memory;
        Profile = profile;
        _fields = profile.Fields;
        _trusted = new HashSet<string>(profile.Fields.Where(f => f.IsTrusted).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        _clusters = Cluster.Build(profile.Fields);
    }

    public VersionProfile Profile { get; }

    public bool CaptureRegions { get; set; }

    /// <summary>Process reads issued by the last <see cref="Read"/> (diagnostics and tests).</summary>
    internal int LastReadCalls { get; private set; }

    public GameSnapshot Read(long sequence, DateTimeOffset now)
    {
        var raw = new Dictionary<string, Field<long>>(_fields.Count, StringComparer.OrdinalIgnoreCase);
        var calls = 0;
        foreach (var cluster in _clusters)
        {
            // One read per cluster of nearby fields instead of one per field; if the block read fails (e.g. a
            // partially unmapped range), fall back to reading each field so one bad page can't hide the rest.
            calls++;
            var block = _memory.TryRead(cluster.Start, cluster.Buffer);
            foreach (var spec in cluster.Fields)
            {
                long v = 0;
                var ok = block;
                if (block)
                {
                    v = spec.Type.Decode(cluster.Buffer.AsSpan((int)(spec.Address - cluster.Start)));
                }
                else
                {
                    calls++;
                    ok = _memory.TryReadValue(spec.Address, spec.Type, out v);
                }

                var value = ok
                    ? Field<long>.Valid(spec.Mask is { } mask ? v & mask : v)
                    : _lastRaw.TryGetValue(spec.Name, out var last) ? last.AsFailedRead() : Field<long>.Unavailable;
                raw[spec.Name] = value;
                _lastRaw[spec.Name] = value;
            }
        }

        var snapshot = Interpret(raw, sequence, now);
        if (snapshot.Location.TryGet(out var room) && snapshot.InGameplay is { HasValue: true, Value: true })
        {
            if (_placedRoom != room || now - _placedAt >= PlacedRefresh)
            {
                _placed = ReadPlacedItems(ref calls);
                _placedRoom = room;
                _placedAt = now;
            }

            snapshot = snapshot with { PlacedItems = _placed };
        }
        if (CaptureRegions && Profile.CaptureRegions.Count > 0)
        {
            var regions = new List<RegionData>(Profile.CaptureRegions.Count);
            foreach (var region in Profile.CaptureRegions)
            {
                calls++;
                var data = new byte[region.Length];
                if (_memory.TryRead(region.Address, data))
                {
                    regions.Add(new RegionData(region.Name, region.Address, data));
                }
            }

            snapshot = snapshot with { Regions = regions };
        }

        LastReadCalls = calls;
        _previous = snapshot;
        return snapshot;
    }

    private Field<IReadOnlyList<PlacedItem>> ReadPlacedItems(ref int calls)
    {
        Span<byte> table = stackalloc byte[ActionEntries * 12];
        calls++;
        if (!_memory.TryRead(ActionTable, table) || !_memory.TryReadU32(ActionTail, out var tailAddress))
        {
            return Field<IReadOnlyList<PlacedItem>>.Unavailable;
        }

        var used = tailAddress >= ActionTable && tailAddress < ActionTable + (ulong)table.Length
            ? (int)((tailAddress - ActionTable) / 12) + 1
            : 0;
        var items = new List<PlacedItem>();
        Span<byte> record = stackalloc byte[12];
        for (var i = 0; i < used; i++)
        {
            var entry = table.Slice(i * 12, 12);
            if (entry[0] != PickupHandler)
            {
                continue;
            }

            calls++;
            if (_memory.TryRead(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), record))
            {
                var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
                var depth = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
                if (width > 0 && depth > 0)
                {
                    items.Add(new PlacedItem(record[8],
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record[2..]), width, depth));
                }
            }
        }

        return Field<IReadOnlyList<PlacedItem>>.Valid(items);
    }

    /// <summary>A contiguous span covering nearby profile fields, read with a single process read.</summary>
    private sealed class Cluster(ulong start, int length, List<FieldSpec> fields)
    {
        /// <summary>Fields further apart than this start a new cluster (don't read large unrelated gaps).</summary>
        private const ulong MaxGap = 0x1000;

        /// <summary>Upper bound for one block read.</summary>
        private const ulong MaxLength = 0x10000;

        public ulong Start { get; } = start;

        public byte[] Buffer { get; } = new byte[length];

        public IReadOnlyList<FieldSpec> Fields { get; } = fields;

        public static Cluster[] Build(IEnumerable<FieldSpec> specs)
        {
            var clusters = new List<Cluster>();
            ulong start = 0, end = 0;
            var current = new List<FieldSpec>();
            foreach (var spec in specs.OrderBy(f => f.Address))
            {
                var fieldEnd = spec.Address + (ulong)spec.Type.SizeOf();
                if (current.Count > 0 && (spec.Address > end + MaxGap || fieldEnd - start > MaxLength))
                {
                    clusters.Add(new Cluster(start, (int)(end - start), current));
                    current = [];
                }

                if (current.Count == 0)
                {
                    start = spec.Address;
                    end = fieldEnd;
                }

                end = Math.Max(end, fieldEnd);
                current.Add(spec);
            }

            if (current.Count > 0)
            {
                clusters.Add(new Cluster(start, (int)(end - start), current));
            }

            return [.. clusters];
        }
    }

    private GameSnapshot Interpret(Dictionary<string, Field<long>> raw, long sequence, DateTimeOffset now)
    {
        var previous = _previous ?? new GameSnapshot();
        var gameplay = DeriveInGame(raw, previous.InGameplay);

        return new GameSnapshot
        {
            Sequence = sequence,
            CapturedAt = now,
            RawFields = raw,
            InGameplay = gameplay,
            Location = Derive(raw, gameplay, previous.Location, [Fields.Stage, Fields.Room], v =>
                v[0] < StageCount && v[1] <= MaxRoom ? new RoomLocation((int)v[0] + 1, (int)v[1]) : null),
            Camera = Derive(raw, gameplay, previous.Camera, [Fields.Camera], v => (int?)v[0]),
            Health = DeriveHealth(raw, gameplay, previous.Health),
            Character = Derive(raw, gameplay, previous.Character, [Fields.Character], v => v[0] switch
            {
                0 => PlayableCharacter.Chris,
                1 => PlayableCharacter.Jill,
                3 => PlayableCharacter.Rebecca,
                _ => PlayableCharacter.Unknown,
            }),
            Position = Derive(raw, gameplay, previous.Position, [Fields.PositionX, Fields.PositionY, Fields.PositionZ], v =>
                new Position((int)v[0], (int)v[1], (int)v[2])),
            GameTime = Derive(raw, gameplay, previous.GameTime, [Fields.GameTimer], v =>
                TimeSpan.FromSeconds(v[0] / (double)FramesPerSecond)),
        };
    }

    /// <summary>
    /// A game is in session when the title menu is not shown, a character has been set up (max HP
    /// is 0 from boot until New Game/Load) and the player is alive. After a death the previous
    /// game's values (e.g. HP -10) stay in memory through character select and the intro, so HP &gt; 0
    /// is required to avoid reporting them. The decomp's 0x02000000 "gameplay" flag was refuted by
    /// live traces and is deliberately not used; see docs/RESEARCH.md.
    /// </summary>
    private Field<bool> DeriveInGame(Dictionary<string, Field<long>> raw, Field<bool> previous)
    {
        if (Trusted(raw, Fields.MainMenu) is not { } menu
            || Trusted(raw, Fields.MaxHp) is not { } maxHp
            || Trusted(raw, Fields.Hp) is not { } hp)
        {
            return Field<bool>.Unsupported;
        }

        if (!menu.IsValid || !maxHp.IsValid || !hp.IsValid)
        {
            return previous.AsFailedRead();
        }

        return Field<bool>.Valid(menu.Value == 0 && maxHp.Value > 0 && hp.Value > 0);
    }

    private Field<Health> DeriveHealth(Dictionary<string, Field<long>> raw, Field<bool> gameplay, Field<Health> previous)
    {
        var health = Derive(raw, gameplay, previous, [Fields.Hp], v => new Health((int)v[0], null));
        if (health.IsValid && Trusted(raw, Fields.MaxHp) is { IsValid: true } max && max.Value > 0)
        {
            return Field<Health>.Valid(health.Value with { Maximum = (int)max.Value });
        }

        return health;
    }

    /// <summary>
    /// Builds a normalized field from trusted raw inputs. Unsupported if any input lacks a trusted profile
    /// field; unavailable outside gameplay or when the value fails sanity checks; stale when a read failed.
    /// </summary>
    private Field<T> Derive<T>(
        Dictionary<string, Field<long>> raw,
        Field<bool> gameplay,
        Field<T> previous,
        string[] inputs,
        Func<long[], T?> map)
        where T : struct
    {
        var values = new long[inputs.Length];
        var readFailed = false;
        for (var i = 0; i < inputs.Length; i++)
        {
            if (Trusted(raw, inputs[i]) is not { } field)
            {
                return Field<T>.Unsupported;
            }

            if (!field.IsValid)
            {
                readFailed = true;
                continue;
            }

            values[i] = field.Value;
        }

        if (readFailed || gameplay.Status is FieldStatus.Stale or FieldStatus.Unavailable)
        {
            return previous.AsFailedRead();
        }

        // Outside gameplay these addresses hold leftovers from the previous session or attract mode.
        if (gameplay is { IsValid: true, Value: false })
        {
            return Field<T>.Unavailable;
        }

        return map(values) is { } mapped ? Field<T>.Valid(mapped) : Field<T>.Unavailable;
    }

    private Field<long>? Trusted(Dictionary<string, Field<long>> raw, string name) =>
        _trusted.Contains(name) && raw.TryGetValue(name, out var value) ? value : null;
}
