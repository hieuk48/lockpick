using System.Globalization;
using System.Text.Json;
using Lockpick.Core.Insight;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>One room-script use of a flag: which room, which character's room file, and what it is (item id, enemy type or door target).</summary>
internal sealed record FlagUse(string Room, string Player, int Flag, int What, string? Target = null);

/// <summary>
/// What RE1's own data says each flag means: the room index (which item, enemy or door uses each flag, generated from
/// the game's room files by tools/rdt), item/enemy names and the flag labels confirmed in play. Embedded from data/.
/// </summary>
internal sealed class Re1Knowledge
{
    private const string ResourcePrefix = "Lockpick.GameAdapter.RE1.Data.";

    private Re1Knowledge(JsonDocument index, JsonDocument names, JsonDocument labels)
    {
        static int Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        static FlagUse Use(JsonElement e, string flag, string what) =>
            new(e.GetProperty("room").GetString()!, e.GetProperty("player").GetString()!, Int(e, flag), Int(e, what),
                e.TryGetProperty("target", out var t) ? t.GetString() : null);
        static Dictionary<int, string> Table(JsonElement e) =>
            e.EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);

        var root = index.RootElement;
        Items = [.. root.GetProperty("items").EnumerateArray().Select(e => Use(e, "flag", "item"))];
        Enemies = [.. root.GetProperty("enemies").EnumerateArray().Select(e => Use(e, "flag", "type"))];
        Doors = [.. root.GetProperty("doors").EnumerateArray().Select(e => Use(e, "lockFlag", "key"))];
        ItemNames = Table(names.RootElement.GetProperty("items"));
        foreach (var (id, name) in Table(names.RootElement.GetProperty("maps")))
        {
            ItemNames[id] = name;
        }

        EnemyNames = Table(names.RootElement.GetProperty("enemies"));
        Labels = labels.RootElement.GetProperty("labels").EnumerateArray()
            .ToDictionary(e => (e.GetProperty("bank").GetString()!, Convert.ToUInt32(e.GetProperty("mask").GetString(), 16)), e => e.GetProperty("label").GetString()!);
    }

    public List<FlagUse> Items { get; }

    public List<FlagUse> Enemies { get; }

    public List<FlagUse> Doors { get; }

    public Dictionary<int, string> ItemNames { get; }

    public Dictionary<int, string> EnemyNames { get; }

    public Dictionary<(string Bank, uint Mask), string> Labels { get; }

    public static Re1Knowledge LoadEmbedded()
    {
        var assembly = typeof(Re1Knowledge).Assembly;
        JsonDocument Load(string file)
        {
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException($"Missing embedded RE1 data {file}.");
            return JsonDocument.Parse(stream);
        }

        using var index = Load("room-index.json");
        using var names = Load("names.json");
        using var labels = Load("flag-labels.json");
        return new Re1Knowledge(index, names, labels);
    }

    public string Item(int id) => ItemNames.TryGetValue(id, out var name) ? name : $"item 0x{id:X2}";

    public string Enemy(int type) => EnemyNames.TryGetValue(type, out var name) ? name : $"enemy type {type}";
}

/// <summary>
/// Explains changes in RE1's flag banks with the room index and the confirmed labels: what's left (story/event flags,
/// unexpected bits, unknown item ids and enemy types) is unexplained. Flag n is bit 31 − n % 32 of word n / 32.
/// </summary>
internal sealed class Re1ChangeExplainer(Re1Knowledge knowledge) : IChangeExplainer
{
    private static readonly (string Bank, int Words, string Kind)[] Banks =
    [
        ("flagRoomItems", 8, "item"), ("flagEnemies", 8, "enemy"), ("flagLocks", 2, "lock"), ("flagRoom", 5, "room"),
        ("flagScenarioA", 4, "story A"), ("flagScenarioB", 8, "story B"),
    ];

    public ChangeReport Explain(GameSnapshot previous, GameSnapshot current)
    {
        if (previous.InGameplay is not { HasValue: true, Value: true } || current.InGameplay is not { HasValue: true, Value: true }
            || !current.Location.TryGet(out var location) || !previous.Location.TryGet(out var previousLocation)
            || (current.GameTime.TryGet(out var now) && previous.GameTime.TryGet(out var before) && now < before))
        {
            return ChangeReport.Empty; // not playing, or a new game / loaded save (everything resets at once)
        }

        var room = location.Id;
        var player = Raw(current, "character") is { } c && (c & 3) == 1 ? "jill" : "chris";
        var unexplained = new List<UnexplainedChange>();
        var known = new List<string>();
        if (previousLocation != location)
        {
            known.Add($"entered room {room}");
        }

        foreach (var (bank, words, kind) in Banks)
        {
            for (var word = 0; word < words; word++)
            {
                var key = $"{bank}{word}";
                if (Raw(previous, key) is not { } old || Raw(current, key) is not { } value || old == value)
                {
                    continue;
                }

                for (var bit = 0; bit < 32; bit++)
                {
                    var mask = 1u << bit;
                    if (((old ^ value) & mask) == 0)
                    {
                        continue;
                    }

                    var set = (value & mask) != 0;
                    var flag = (word * 32) + (31 - bit);
                    if (Explain(bank, word, mask, flag, set, room, player) is { } explanation)
                    {
                        if (explanation.Length > 0)
                        {
                            known.Add(explanation);
                        }
                    }
                    else
                    {
                        unexplained.Add(new UnexplainedChange(
                            $"{key}:0x{mask:X8}:{(set ? "set" : "clear")}",
                            $"{kind} flag #{flag} {(set ? "set" : "cleared")} ({key} bit 0x{mask:X}) in room {room}"));
                    }
                }
            }
        }

        // Ids the name tables don't know. Enemies only once the room has loaded (same room in both polls).
        for (var slot = 0; slot < 8; slot++)
        {
            if (Raw(current, $"inv{slot}") is { } entry && (entry & 0xFF) is var id and > 0 && !knowledge.ItemNames.ContainsKey((int)id))
            {
                unexplained.Add(new UnexplainedChange($"item:0x{id:X2}", $"unknown item id 0x{id:X2} in inventory slot {slot + 1}"));
            }

            if (previousLocation == location && Raw(current, $"enemy{slot}Flags") is { } flags && (flags & 1) != 0
                && Raw(current, $"enemy{slot}Type") is { } type && !knowledge.EnemyNames.ContainsKey((int)type))
            {
                unexplained.Add(new UnexplainedChange($"enemy:{type}", $"unknown enemy type {type} in room {room}"));
            }
        }

        return new ChangeReport(unexplained, known);
    }

    /// <summary>Flags from here on in the room bank are maps taken (124 = 1F, 125 = 2F, 126 = garden) and documents.</summary>
    private const int FirstMapFlag = 124;

    /// <summary>
    /// Where each stage's rooms start in the rooms-visited flags: one flag per room, numbered on across stages
    /// (live 2026-10-13: 100 = 0, 200 = 32, 300 = 63, 400 = 82). Later stages are not mapped yet.
    /// </summary>
    private static readonly int[] StageVisitBase = [0, 32, 63, 82];

    private static string VisitedRoom(int flag)
    {
        var stage = Array.FindLastIndex(StageVisitBase, b => b <= flag);
        var room = flag - StageVisitBase[stage];
        return stage == StageVisitBase.Length - 1 && room > 0x1F ? $"(flag {flag})" : $"{stage + 1}{room:X2}";
    }

    private static uint? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? (uint)value.Value : null;

    private static FlagUse? Find(List<FlagUse> uses, int flag, string room, string player)
    {
        var matches = uses.Where(u => u.Flag == flag && (u.Player == player || u.Player == "both")).ToList();
        return matches.Find(u => u.Room == room || u.Target == room) ?? matches.FirstOrDefault();
    }

    /// <summary>A description of an explained change ("" when not worth showing), or null when it is unexplained.</summary>
    private string? Explain(string bank, int word, uint mask, int flag, bool set, string room, string player)
    {
        if (knowledge.Labels.TryGetValue(($"{bank}{word}", mask), out var label))
        {
            return set ? label : string.Empty;
        }

        switch (bank)
        {
            case "flagRoomItems" when Find(knowledge.Items, flag, room, player) is { } item:
                return set ? $"put {knowledge.Item(item.What)} back" : $"picked up {knowledge.Item(item.What)}";
            case "flagEnemies" when set && Find(knowledge.Enemies, flag, room, player) is { } enemy:
                return $"killed {knowledge.Enemy(enemy.What)}";
            case "flagLocks" when Find(knowledge.Doors, flag, room, player) is { } door:
                return $"{(set ? "unlocked" : "locked")} door {door.Room}→{door.Target}";

            // Rooms visited (the map's "explored" marks): flags below FirstMapFlag, set on the first visit.
            case "flagRoom" when set && flag < FirstMapFlag:
                return $"first visit to room {VisitedRoom(flag)}";
            default:
                return null;
        }
    }
}
