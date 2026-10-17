using System.Text;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

public sealed class Re1RunRulesTests : IDisposable
{
    private readonly Re1RunRules _rules = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-lss-" + Guid.NewGuid().ToString("N"));

    public Re1RunRulesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A LiveSplit file shaped like the community's (speedrun.com resources): names only, no times.</summary>
    private static string Lss(string category, params string[] names) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Run version="1.7.0">
          <GameName>Resident Evil</GameName>
          <CategoryName>{category}</CategoryName>
          <AttemptCount>0</AttemptCount>
          <Segments>
        {string.Concat(names.Select(n => $"    <Segment><Name>{n}</Name><SplitTimes><SplitTime name=\"Personal Best\" /></SplitTimes><BestSegmentTime /></Segment>\n"))}
          </Segments>
        </Run>
        """;

    private static readonly string[] JillGoodNmg =
    [
        "King Henry", "Yawn 1", "Mansion 1", "Courtyard 1", "Boxes", "Guardhouse", "Courtyard 2", "Yawn 2", "Basement", "Mansion 2",
        "Courtyard 3", "Black Tiger", "Underground", "Loud Room", "Lab Tyrant", "Orgy Room", "End",
    ];

    [Fact]
    public void The_built_in_sets_match_the_community_s_split_counts()
    {
        var sets = _rules.SplitSets;
        Assert.Equal(10, sets.Count);
        Assert.All(sets.Where(s => s.Id.EndsWith("-segments", StringComparison.Ordinal)), s =>
            Assert.Equal(s.Category.Contains("Good", StringComparison.Ordinal) ? 17 : 16, s.Splits.Count));
        Assert.All(sets.Where(s => s.Id.EndsWith("-key-items", StringComparison.Ordinal)), s => Assert.Equal(27, s.Splits.Count));
        Assert.All(sets, s => Assert.IsType<RunFinished>(s.Splits[^1].Trigger));
        Assert.All(sets, s => Assert.False(s.Verified));
    }

    [Fact]
    public void Segments_use_the_autosplitter_s_room_transitions()
    {
        var jill = _rules.SplitSets.Single(s => s.Id == "re1-jill-bad-nmg-segments");
        Assert.Equal(new RoomEntered("203", "204"), jill.Splits[0].Trigger);              // King Henry
        Assert.Equal(new RoomEntered("300", FromOtherStage: true), jill.Splits[2].Trigger); // Mansion 1
        Assert.Equal(new RoomEntered("610", FromOtherStage: true), jill.Splits[8].Trigger); // Basement (bad ending)
        var good = _rules.SplitSets.Single(s => s.Id == "re1-jill-good-nmg-segments");
        Assert.Equal(new RoomEntered("71B", "71C"), good.Splits[8].Trigger);               // Basement (good ending)
        Assert.Equal(new RoomEntered("508", "509"), good.Splits[15].Trigger);              // Orgy Room
    }

    [Fact]
    public void Key_items_that_come_more_than_once_are_tied_to_their_room()
    {
        var route = _rules.SplitSets.Single(s => s.Id == "re1-jill-nmg-key-items");
        Assert.Equal(new ItemObtained(31), route.Splits[0].Trigger);
        Assert.Equal(new ItemObtained(39, "719"), route.Splits.Single(s => s.Name == "Battery (Courtyard)").Trigger);
        Assert.Equal(new ItemObtained(39, "501"), route.Splits.Single(s => s.Name == "Battery (Elevator)").Trigger);
    }

    [Fact]
    public void A_community_file_gets_the_triggers_of_its_own_category()
    {
        var path = Path.Combine(_folder, "Resident Evil - Jill - Good Ending - NMG.lss");
        File.WriteAllText(path, Lss("Jill - Good Ending - NMG", JillGoodNmg), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var run = LiveSplitFile.Read(path);
        var set = LiveSplitFile.ToSplitSet(run, path, _rules.SplitSets);

        Assert.Equal(PlayableCharacter.Jill, set.Character);
        Assert.Equal(17, set.Splits.Count);
        Assert.Equal(new RoomEntered("71B", "71C"), set.Splits[8].Trigger); // the good ending's Basement, not the bad one's
        Assert.DoesNotContain(set.Splits, s => s.Trigger is ManualSplit);
        Assert.Equal(path, set.ImportedFrom);
    }

    [Fact]
    public void Names_lockpick_doesn_t_know_become_split_key_splits()
    {
        var path = Path.Combine(_folder, "mine.lss");
        File.WriteAllText(path, Lss("Chris Any% NMG", "Sword Key", "My own split", "002 key", "End"));
        var set = LiveSplitFile.ToSplitSet(LiveSplitFile.Read(path), path, _rules.SplitSets);

        Assert.Equal(new ItemObtained(51), set.Splits[0].Trigger);
        Assert.IsType<ManualSplit>(set.Splits[1].Trigger);
        Assert.Equal(new ItemObtained(57), set.Splits[2].Trigger); // "002-Key" matches without punctuation
        Assert.IsType<RunFinished>(set.Splits[3].Trigger);
    }

    [Fact]
    public void Your_own_splits_are_saved_and_read_back_exactly()
    {
        var jill = _rules.SplitSets.Single(s => s.Id == "re1-jill-bad-nmg-segments");
        var edited = jill with
        {
            Name = "My Jill route",
            Splits = [.. jill.Splits.Take(2), new SplitDefinition("Battery", new ItemObtained(39, "719")), new SplitDefinition("Check", new ManualSplit()), jill.Splits[^1]],
        };
        var path = Path.Combine(_folder, SplitSetFile.FileNameFor("My Jill route!") + ".json");
        SplitSetFile.Write(path, edited);
        var back = SplitSetFile.Read(path);

        Assert.Equal("mine-my-jill-route", back.Id);
        Assert.Equal("My Jill route", back.Name);
        Assert.Equal(SplitSetFile.Custom, back.Source);
        Assert.Equal(PlayableCharacter.Jill, back.Character);
        Assert.Equal(edited.Splits, back.Splits); // names and triggers, room-from and other-area flags included
        Assert.False(back.Verified);
    }

    [Fact]
    public void A_splits_file_without_splits_is_refused()
    {
        var path = Path.Combine(_folder, "empty.json");
        File.WriteAllText(path, "{\"name\": \"x\", \"splits\": []}");
        Assert.Throws<InvalidDataException>(() => SplitSetFile.Read(path));
    }

    [Fact]
    public void Files_with_a_dtd_are_refused()
    {
        var path = Path.Combine(_folder, "evil.lss");
        File.WriteAllText(path, """<?xml version="1.0"?><!DOCTYPE Run [<!ENTITY x "boom">]><Run><Segments><Segment><Name>&x;</Name></Segment></Segments></Run>""");
        Assert.Throws<InvalidDataException>(() => LiveSplitFile.Read(path));
    }

    [Fact]
    public void Exported_files_carry_the_personal_best_in_game_time()
    {
        var set = _rules.SplitSets.Single(s => s.Id == "re1-jill-bad-nmg-segments");
        var t0 = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var splits = set.Splits.Select((s, i) => new RecordedSplit(s.Name, TimeSpan.FromMinutes(i + 1), TimeSpan.FromMinutes(i + 1.5))).ToList();
        var record = new RunRecord(set.Id, set.Name, set.Category, PlayableCharacter.Jill, 1, t0, t0.AddMinutes(20), RunResult.Finished,
            TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(20), splits, false, 0, false, "re1-gog-usa-hotfix4");
        var path = Path.Combine(_folder, "out.lss");
        LiveSplitFile.Write(path, "Resident Evil", set, [record]);

        var back = LiveSplitFile.Read(path);
        Assert.Equal("Jill - Bad Ending - NMG", back.CategoryName);
        Assert.Equal(1, back.AttemptCount);
        Assert.Equal(set.Splits.Select(s => s.Name), back.Segments.Select(s => s.Name));
        Assert.Equal(TimeSpan.FromMinutes(16), back.Segments[^1].PersonalBestGameTime);
        Assert.Equal(TimeSpan.FromMinutes(1), back.Segments[1].BestSegmentGameTime);
        Assert.Equal([.. back.Segments.Select(s => s.PersonalBestGameTime)], LiveSplitFile.ComparisonOf(back).PersonalBest);
    }

    private static GameSnapshot Raw(long hp, long maxHp, long menu, long flags = 0x8000000C, long stage = 0) => new()
    {
        RawFields = new Dictionary<string, Field<long>>
        {
            ["hp"] = Field<long>.Valid(hp),
            ["maxHp"] = Field<long>.Valid(maxHp),
            ["mainMenu"] = Field<long>.Valid(menu),
            ["stateFlags"] = Field<long>.Valid(flags),
            ["stage"] = Field<long>.Valid(stage),
        },
    };

    [Theory]
    [InlineData(-10, 96, 0, true)]   // a kill overshoots below 0
    [InlineData(-1, 140, 0, true)]   // the dog's finisher, a Hunter's leap
    [InlineData(0, 96, 0, false)]    // HP 0 is still alive (a normal wasp's sting)
    [InlineData(1, 96, 0, false)]
    [InlineData(-10, 0, 0, false)]   // boot: no game yet
    [InlineData(-10, 96, 1, false)]  // the title menu after a death
    public void A_death_is_hp_below_zero_in_a_game(int hp, int maxHp, int menu, bool dead) =>
        Assert.Equal(dead, _rules.PlayerDied(Raw(hp, maxHp, menu)));

    [Fact]
    public void The_ending_is_the_state_flags_dropping_to_zero_at_the_heliport()
    {
        Assert.True(_rules.RunFinished(Raw(50, 96, 0, 0x8000000C, 2), Raw(50, 96, 0, 0, 2)));
        Assert.False(_rules.RunFinished(Raw(50, 96, 0, 0, 2), Raw(50, 96, 0, 0, 2)));          // already 0: no new ending
        Assert.False(_rules.RunFinished(Raw(50, 96, 0, 0x8000000C, 0), Raw(50, 96, 0, 0, 0))); // elsewhere
    }

    [Fact]
    public void The_install_check_finds_changed_missing_and_extra_files_and_ignores_what_changes_in_use()
    {
        var install = Path.Combine(_folder, "install");
        Directory.CreateDirectory(Path.Combine(install, "SAVE"));
        File.WriteAllText(Path.Combine(install, "ResidentEvil.exe"), "not the real one");
        File.WriteAllText(Path.Combine(install, "mod.asi"), "a plugin");
        File.WriteAllText(Path.Combine(install, "dxcfg.ini"), "[dxcfg]");           // the player's display settings
        File.WriteAllText(Path.Combine(install, "SAVE", "savedat1.dat"), "save");    // saves
        File.WriteAllText(Path.Combine(install, "Mon Oct 12.BMP"), "screenshot");    // screenshots

        var check = _rules.CheckInstall(install);
        Assert.False(check.Unchanged);
        Assert.Equal(["ResidentEvil.exe"], check.Changed);
        Assert.Contains("ddraw.dll", check.Missing);
        Assert.Equal(["mod.asi"], check.Extra);
        Assert.Equal("GOG 1.0 hotfix4", check.Release);
    }

    [Fact]
    public void The_official_files_list_has_the_executable_the_profile_knows()
    {
        var manifest = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "games", "re1", "install", "gog-hotfix4.sha256"));
        Assert.Contains("205BD6FED6E080A38823958E5C3306CACC86A18FF9A2758179E8D33CAEEE4A78  ResidentEvil.exe", manifest);
        Assert.True(manifest.Length > 2900);
    }

    [Fact]
    public void The_title_menu_is_the_main_menu_flag()
    {
        Assert.True(_rules.AtTitle(Raw(0, 0, 1)));
        Assert.False(_rules.AtTitle(Raw(96, 96, 0)));
    }
}
