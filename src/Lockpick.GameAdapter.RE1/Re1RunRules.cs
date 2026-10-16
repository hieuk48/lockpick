using System.Text.Json;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// What runs need to know about RE1 (docs/RUNS.md): the built-in split sets (data/games/re1/splits.json), a death,
/// the title menu and the ending.
/// </summary>
public sealed class Re1RunRules : IRunRules
{
    private const string Resource = "Lockpick.GameAdapter.RE1.Data.splits.json";

    /// <summary>Stage index (0-based) of the heliport, where the ending starts.</summary>
    private const int EndingStage = 2;

    private static readonly Lazy<IReadOnlyList<SplitSet>> Sets = new(LoadEmbedded);

    public IReadOnlyList<SplitSet> SplitSets => Sets.Value;

    /// <summary>
    /// HP below 0 in a game (docs/MEMORY_MAP.md: every kill leaves HP below 0; poison and wasps clamp at 1). The state
    /// flag 0x01000000 comes with it but may lag the HP; the HP alone is enough. Read from the raw fields: once HP is
    /// below 0 the snapshot no longer counts as in game.
    /// </summary>
    public bool PlayerDied(GameSnapshot snapshot) =>
        Raw(snapshot, Re1Session.Fields.MainMenu) == 0
        && Raw(snapshot, Re1Session.Fields.MaxHp) > 0
        && Raw(snapshot, Re1Session.Fields.Hp) is < 0;

    /// <summary>The title / main menu flag (runtime-verified field <c>mainMenu</c>).</summary>
    public bool AtTitle(GameSnapshot snapshot) => Raw(snapshot, Re1Session.Fields.MainMenu) == 1;

    /// <summary>
    /// The final cutscene: the state flags drop to 0 at the heliport (the community autosplitter's final split, RE1aio.asl).
    /// Not yet seen live by Lockpick (capture bounty B703).
    /// </summary>
    public bool RunFinished(GameSnapshot previous, GameSnapshot current) =>
        Raw(current, Re1Session.Fields.StateFlags) == 0
        && Raw(previous, Re1Session.Fields.StateFlags) is not (null or 0)
        && Raw(current, Re1Session.Fields.Stage) == EndingStage
        && Raw(current, Re1Session.Fields.MainMenu) == 0;

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.TryGet(out var v) ? v : null;

    internal static IReadOnlyList<SplitSet> LoadEmbedded()
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing embedded RE1 data {Resource}.");
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("sets").EnumerateArray().Select(Set)];
    }

    private static SplitSet Set(JsonElement set)
    {
        var character = set.GetProperty("character").GetString() switch
        {
            "jill" => PlayableCharacter.Jill,
            "chris" => PlayableCharacter.Chris,
            "rebecca" => PlayableCharacter.Rebecca,
            _ => PlayableCharacter.Unknown,
        };
        var splits = set.GetProperty("splits").EnumerateArray().Select(s => new SplitDefinition(s.GetProperty("name").GetString()!, Trigger(s))).ToList();
        return new SplitSet(set.GetProperty("id").GetString()!, set.GetProperty("name").GetString()!, set.GetProperty("category").GetString()!,
            character, splits, Verified: false, Source: "Built in (the community's splits, not yet checked in a full run)");
    }

    private static SplitTrigger Trigger(JsonElement split)
    {
        if (split.TryGetProperty("end", out var end) && end.GetBoolean())
        {
            return new RunFinished();
        }

        if (split.TryGetProperty("item", out var item))
        {
            return new ItemObtained(item.GetInt32(), split.TryGetProperty("in", out var room) ? room.GetString() : null);
        }

        if (split.TryGetProperty("room", out var to))
        {
            return new RoomEntered(to.GetString()!,
                split.TryGetProperty("from", out var from) ? from.GetString() : null,
                split.TryGetProperty("fromOtherStage", out var other) && other.GetBoolean());
        }

        return new ManualSplit();
    }
}
