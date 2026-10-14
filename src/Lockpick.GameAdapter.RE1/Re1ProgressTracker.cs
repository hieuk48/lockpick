using Lockpick.Core.Insight;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// The player's progress, from the flag banks alone (so it works from a loaded save): items still in the world vs taken
/// (pickup flags, 1 = still there), documents and maps, rooms explored (the game's rooms-visited table), enemies killed
/// for good (kill flags) and the named story events. Read-only. Flag n is bit 31 − n % 32 of word n / 32.
/// </summary>
internal sealed class Re1ProgressTracker(Re1Knowledge knowledge) : IProgressTracker
{
    private const int FirstMapItem = 0x4E;
    private const int FirstNpcType = 32;
    private const int NotRemembered = 255;

    /// <summary>Room "items" that are never picked up: Jill's lockpick is handed over by Barry in a cutscene.</summary>
    private static readonly HashSet<int> NotPickups = [49];

    // Jill's room files list ink ribbons, but the game never places them for her (no ribbons in her game: none of her
    // 20 ribbon spots was ever armed in the 2026-10-14 pickup sweep across every story phase; Chris picked up 10 of 14).
    private const int InkRibbon = 0x2F;

    /// <summary>The game's rooms-visited table (0x4D31E0 / 0x4D31E8): first flag and room count per stage 1–5.</summary>
    private static readonly (int Base, int Rooms)[] VisitTable = [(0, 32), (32, 31), (63, 19), (82, 18), (100, 24)];

    public ProgressReport? Evaluate(GameSnapshot snapshot)
    {
        if (snapshot.InGameplay is not { HasValue: true, Value: true })
        {
            return null;
        }

        var player = Raw(snapshot, "character") is { } c && (c & 3) == 1 ? "jill" : "chris";
        bool Mine(FlagUse u) => u.Player == player || u.Player == "both";

        // One entry per flag (a flag can appear in several rooms' scripts); the first room listed is where it is.
        var items = knowledge.Items.Where(u => Mine(u) && u.Flag >= 0 && !NotPickups.Contains(u.What)
                && !(player == "jill" && u.What == InkRibbon))
            .GroupBy(u => u.Flag).Select(g => g.First()).ToList();
        var enemies = knowledge.Enemies.Where(u => Mine(u) && u.Flag is >= 0 and not NotRemembered && u.What < FirstNpcType)
            .GroupBy(u => u.Flag).Select(g => g.First()).ToList();

        var areas = new List<AreaProgress>();
        foreach (var (stage, area) in knowledge.Areas.OrderBy(a => a.Key))
        {
            var groups = new List<ProgressGroup>();
            bool InArea(FlagUse u) => u.Room[0] == stage;

            var pickups = items.Where(u => InArea(u) && u.What < FirstMapItem)
                .Select(u => new ProgressEntry(knowledge.Item(u.What), knowledge.RoomLabel(u.Room), !Bit(snapshot, "flagRoomItems", u.Flag)))
                .ToList();
            var papers = items.Where(u => InArea(u) && u.What >= FirstMapItem)
                .Select(u => new ProgressEntry(Paper(u.What), knowledge.RoomLabel(u.Room), !Bit(snapshot, "flagRoomItems", u.Flag)))
                .ToList();
            var kills = enemies.Where(InArea)
                .Select(u => new ProgressEntry(knowledge.Enemy(u.What), knowledge.RoomLabel(u.Room), Bit(snapshot, "flagEnemies", u.Flag)))
                .ToList();

            // Rooms explored: stages 6 and 7 (the mansion revisited) share the 1F/2F marks, so they're listed there.
            var visits = new List<ProgressEntry>();
            if (stage is >= '1' and <= '5')
            {
                var (first, count) = VisitTable[stage - '1'];
                for (var room = 0; room < count; room++)
                {
                    var id = $"{stage}{room:X2}";
                    visits.Add(new ProgressEntry(knowledge.RoomLabel(id), id, Bit(snapshot, "flagRoom", first + room)));
                }
            }

            if (pickups.Count > 0)
            {
                groups.Add(new ProgressGroup("Items", pickups));
            }

            if (papers.Count > 0)
            {
                groups.Add(new ProgressGroup("Documents and maps", papers));
            }

            if (visits.Count > 0)
            {
                groups.Add(new ProgressGroup("Rooms explored", visits));
            }

            if (kills.Count > 0)
            {
                groups.Add(new ProgressGroup("Enemies defeated", kills));
            }

            if (groups.Count > 0)
            {
                areas.Add(new AreaProgress(area, groups));
            }
        }

        var story = knowledge.LabelOrder
            .Where(l => l.Bank.StartsWith("flagScenario", StringComparison.Ordinal) && (Raw(snapshot, l.Bank) is { } v) && (v & l.Mask) != 0)
            .Select(l => l.Label).Distinct().ToList();
        var unnamed = 0;
        foreach (var (bank, words) in new[] { ("flagScenarioA", 4), ("flagScenarioB", 8) })
        {
            for (var word = 0; word < words; word++)
            {
                if (Raw(snapshot, $"{bank}{word}") is { } value)
                {
                    for (var bit = 0; bit < 32; bit++)
                    {
                        if ((value & (1u << bit)) != 0 && !knowledge.Labels.ContainsKey(($"{bank}{word}", 1u << bit)))
                        {
                            unnamed++;
                        }
                    }
                }
            }
        }

        return new ProgressReport(areas, story, unnamed);
    }

    private string Paper(int item) =>
        knowledge.ItemNames.TryGetValue(item, out var map) ? map
        : knowledge.Documents.TryGetValue(item, out var doc) ? doc
        : $"document {item}";

    private static uint? Raw(GameSnapshot s, string field) =>
        s.RawFields.TryGetValue(field, out var value) && value.HasValue ? (uint)value.Value : null;

    private static bool Bit(GameSnapshot s, string bank, int flag) =>
        Raw(s, $"{bank}{flag / 32}") is { } word && (word & (1u << (31 - (flag % 32)))) != 0;
}
