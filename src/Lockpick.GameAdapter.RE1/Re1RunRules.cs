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
    private static readonly Lazy<LeaderboardDefinition> Board = new(LoadBoard);

    public IReadOnlyList<SplitSet> SplitSets => Sets.Value;

    /// <summary>RE1 PC on speedrun.com (data/games/re1/speedrun-com.json).</summary>
    public LeaderboardDefinition Leaderboard => Board.Value;

    /// <summary>
    /// The board from the split set's category and name: Jill or Chris, the ending (an any% key-item route ends badly:
    /// the fastest ending), glitches, knife only. What a name doesn't say is left for the player to pick.
    /// </summary>
    public IReadOnlyDictionary<string, string> LeaderboardFor(SplitSet splits)
    {
        var text = $"{splits.Category} {splits.Name}";
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        var board = new Dictionary<string, string>();
        if (splits.Character is PlayableCharacter.Jill or PlayableCharacter.Chris)
        {
            board["character"] = splits.Character == PlayableCharacter.Jill ? "jill" : "chris";
        }

        if (Has("Good Ending") || Has("Best Ending"))
        {
            board["ending"] = "best";
        }
        else if (Has("Bad Ending") || Has("Any%"))
        {
            board["ending"] = "bad";
        }

        if (Has("NMG") || Has("No Major"))
        {
            board["glitches"] = "nmg";
        }
        else if (Has("MG") || Has("Major Glitches"))
        {
            board["glitches"] = "mg";
        }

        board["restriction"] = Has("Knife") ? "knife" : "none";
        return board;
    }

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

    private static LeaderboardDefinition LoadBoard()
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.speedrun-com.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data speedrun-com.json.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;
        return new LeaderboardDefinition(S(root, "game"), S(root, "category"), S(root, "platform"), S(root, "timeField"), S(root, "weblink"),
            [.. root.GetProperty("filters").EnumerateArray().Select(f => new LeaderboardFilter(S(f, "key"), S(f, "label"), S(f, "variable"),
                [.. f.GetProperty("values").EnumerateArray().Select(v => new LeaderboardChoice(S(v, "key"), S(v, "label"), S(v, "id")))]))]);
    }

    /// <summary>
    /// The install against the files GOG's 1.0 hotfix4 release ships (data/games/re1/install/gog-hotfix4.sha256, made
    /// by tools/re1/install_manifest.py from an untouched install). Files that change in normal use are not compared:
    /// saves, the display settings, screenshots, GOG's log and uninstaller data.
    /// </summary>
    public InstallCheck CheckInstall(string installDirectory)
    {
        var expected = Manifest.Value;
        var changed = new List<string>();
        var missing = new List<string>();
        foreach (var (relative, digest) in expected)
        {
            var path = Path.Combine(installDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing.Add(relative);
                continue;
            }

            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(relative);
            }
        }

        var known = expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(installDirectory, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(r => !known.Contains(r) && !Varies(r))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new InstallCheck("GOG 1.0 hotfix4", changed, missing, extra);
    }

    /// <summary>The PC leaderboard's rules that only the player can keep (speedrun.com, RE1 PC rules, read 2026-10-16).</summary>
    public IReadOnlyList<string> LeaderboardReminders { get; } =
    [
        "Start the run at the title screen.",
        "Show LiveSplit's in-game time script on screen (the rules require it), and the final in-game time on the end screen in your video.",
        "Don't tab out or click off the game during the run.",
        "Don't open the options menu as a pause (it pauses the in-game time).",
        "No music over the game, no mods, no turbo or macros, no auto-aim cancel.",
        "With Classic REbirth: quickturn and tactical reload off, and don't open its debug menu.",
    ];

    private static readonly Lazy<Dictionary<string, string>> Manifest = new(() =>
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.gog-hotfix4.sha256")
            ?? throw new InvalidOperationException("Missing embedded RE1 data gog-hotfix4.sha256.");
        using var reader = new StreamReader(stream);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 66 && line[64] == ' ')
            {
                files[line[66..]] = line[..64];
            }
        }

        return files;
    });

    /// <summary>Files that change in normal use (as tools/re1/install_manifest.py leaves them out).</summary>
    private static bool Varies(string relative)
    {
        var name = Path.GetFileName(relative).ToLowerInvariant();
        return relative.StartsWith("SAVE/", StringComparison.OrdinalIgnoreCase)
            || name is "dxcfg.ini" or "dxcfg.ini.bak" or "goglog.ini" or "unins000.dat" or "unins000.ini" or "unins000.msg" or "webcache.zip"
            || name.EndsWith(".bmp", StringComparison.Ordinal);
    }

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
