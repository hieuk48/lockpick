using System.Text.Json.Serialization;

namespace Lockpick.Core.State;

/// <summary>Playable character, normalized across games.</summary>
public enum PlayableCharacter
{
    Unknown = 0,
    Chris,
    Jill,
    Rebecca,
    Leon,
    Claire,
    Sherry,
    Hunk,
    Tofu,
    Carlos,
}

/// <summary>A room inside a game stage. Stage is the human-facing (1-based) stage number.</summary>
public readonly record struct RoomLocation(int Stage, int Room)
{
    /// <summary>Compact identifier matching the game's room file naming, e.g. stage 1 room 0x06 → "106".</summary>
    [JsonIgnore]
    public string Id => $"{Stage}{Room:X2}";

    public override string ToString() => Id;
}

/// <summary>An item the game has placed in the current room right now (adapter-specific id; zone in room units).</summary>
public sealed record PlacedItem(int ItemId, int X, int Z, int Width, int Depth);

/// <summary>What a live actor is, for drawing: an ordinary enemy, a boss, or a non-player character (partners etc.).</summary>
public enum ActorKind
{
    Enemy,
    Boss,
    Npc,
}

/// <summary>An enemy or NPC in the current room right now (adapter-specific type id; room units; raw facing).</summary>
public sealed record LiveActor(int Slot, int Type, string Name, ActorKind Kind, int X, int Z, int Angle, int Hp);

/// <summary>World position in game units. Orientation is kept raw because units differ per game.</summary>
public readonly record struct Position(int X, int Y, int Z)
{
    public override string ToString() => $"{X}, {Y}, {Z}";
}

/// <summary>A verbatim copy of one capture region at the time of a snapshot.</summary>
public sealed record RegionData(string Name, ulong Address, byte[] Data);

/// <summary>Player health. <see cref="Maximum"/> is only set when the adapter knows it reliably.</summary>
public readonly record struct Health(int Current, int? Maximum)
{
    public override string ToString() => Maximum is { } max ? $"{Current}/{max}" : Current.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Normalized, game-agnostic view of the game at one point in time. Every value is a
/// <see cref="Field{T}"/> so consumers can tell verified data from unsupported or stale data.
/// </summary>
public sealed record GameSnapshot
{
    /// <summary>Monotonic poll counter within one attachment.</summary>
    public long Sequence { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>True while a game is in session (not boot, title screen or attract mode). In-game menus still count as in game.</summary>
    public Field<bool> InGameplay { get; init; } = Field<bool>.Unsupported;

    public Field<PlayableCharacter> Character { get; init; } = Field<PlayableCharacter>.Unsupported;

    public Field<Health> Health { get; init; } = Field<Health>.Unsupported;

    public Field<RoomLocation> Location { get; init; } = Field<RoomLocation>.Unsupported;

    public Field<int> Camera { get; init; } = Field<int>.Unsupported;

    public Field<Position> Position { get; init; } = Field<Position>.Unsupported;

    public Field<TimeSpan> GameTime { get; init; } = Field<TimeSpan>.Unsupported;

    /// <summary>
    /// Verbatim copies of the profile's capture regions, filled only while a capture asks for them
    /// (<see cref="Monitoring.IGameStateSource.RecordRegions"/>). Never serialized into recordings.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<RegionData> Regions { get; init; } = [];

    /// <summary>Raw values of profile fields keyed by field name, for diagnostics only.</summary>
    public IReadOnlyDictionary<string, Field<long>> RawFields { get; init; } = new Dictionary<string, Field<long>>();

    /// <summary>
    /// Items the game has actually placed in the current room (story conditions applied, taken ones gone), when the
    /// adapter can read them; not recorded.
    /// </summary>
    [JsonIgnore]
    public Field<IReadOnlyList<PlacedItem>> PlacedItems { get; init; } = Field<IReadOnlyList<PlacedItem>>.Unsupported;

    /// <summary>Enemies and NPCs active in the current room (for the map; not recorded in captures, whose regions hold
    /// the raw slots).</summary>
    [JsonIgnore]
    public Field<IReadOnlyList<LiveActor>> Actors { get; init; } = Field<IReadOnlyList<LiveActor>>.Unsupported;

    /// <summary>
    /// Compares the game-facing content of two snapshots, ignoring sequence, timestamp and raw diagnostics.
    /// </summary>
    public bool HasSameStateAs(GameSnapshot? other) =>
        other is not null
        && InGameplay == other.InGameplay
        && Character == other.Character
        && Health == other.Health
        && Location == other.Location
        && Camera == other.Camera
        && Position == other.Position
        && GameTime == other.GameTime;
}
