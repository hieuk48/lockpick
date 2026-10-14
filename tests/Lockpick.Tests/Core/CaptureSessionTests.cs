using System.IO.Compression;
using System.Text.Json;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.Recording;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.Core;

/// <summary>End-to-end capture: real RE1 adapter over fake memory, driven through the real monitor.</summary>
public sealed class CaptureSessionTests : IDisposable
{
    private const string ExePath = @"C:\Users\someone\Games\Resident Evil\ResidentEvil.exe";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lockpick-capture-test-{Guid.NewGuid():N}");
    private readonly Re1Adapter _adapter = new();
    private readonly FakeProcessHost _host = new();
    private readonly FakeFingerprintProvider _fingerprints = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Records_traces_recordings_events_and_build_info_then_zips()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        var memory = new FakeMemory().WithVerifiedImage(profile);
        void Set(string field, uint value, int size = 1)
        {
            var address = profile.FindField(field)!.Address;
            if (size == 1)
            {
                memory.WriteU8(address, (byte)value);
            }
            else if (size == 2)
            {
                memory.WriteI16(address, (short)value);
            }
            else
            {
                memory.WriteU32(address, value);
            }
        }

        // Chris in the main hall with a knife and a Beretta.
        Set("room", 0x06);
        Set("hp", 140, 2);
        Set("maxHp", 140);
        Set("gameTimer", 300, 4);
        Set("inv0", 0x0001, 2);
        Set("inv1", 0x0F02, 2);

        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        var process = _host.Launch(4242, ExePath, memory, new ModuleImage(0x400000, 0x49B2000, ExePath));

        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick(); // attach + first poll
        Assert.True(capture.IsRecordingGame);

        Set("inv1", 0x0E02, 2); // fired one shot
        Set("gameTimer", 330, 4);
        monitor.Tick();
        Set("room", 0x05); // through the dining-room door
        monitor.Tick();

        _host.Exit(4242);
        monitor.Tick(); // detach
        Assert.False(capture.IsRecordingGame);
        Assert.True(process.Disposed);

        var zip = capture.Stop();
        Assert.True(File.Exists(zip));

        using var archive = ZipFile.OpenRead(zip);
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var trace = Read("trace-1.txt");
        Assert.Contains("inv1=0xE02", trace, StringComparison.Ordinal);
        Assert.Contains("room=0x5", trace, StringComparison.Ordinal);
        Assert.Contains("timer=0x14A", trace, StringComparison.Ordinal);

        Assert.Contains("Room 106 → 105", Read("events-1.txt"), StringComparison.Ordinal);

        var (header, frames) = SessionRecording.Read(new StringReader(Read("recording-1.jsonl")));
        Assert.Equal("re1-gog-usa-hotfix4", header.ProfileId);
        Assert.Equal(3, frames.Count); // one per poll here: the timer advanced, then the room changed

        var infoText = Read("capture-info.json");
        Assert.DoesNotContain("someone", infoText, StringComparison.Ordinal); // no paths / user names
        var info = JsonSerializer.Deserialize<CaptureInfo>(infoText)!;
        Assert.Equal("9.9.9", info.LockpickVersion);
        Assert.Equal(1, info.Attachments);
        Assert.Equal(3, info.Samples);
        var build = Assert.Single(info.Builds);
        Assert.Equal("ResidentEvil.exe", build.FileName);
        Assert.Equal("re1-gog-usa-hotfix4", build.ProfileId);
        Assert.NotNull(info.EndedAt);
    }

    [Fact]
    public void Capture_started_mid_session_records_the_current_state_first()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        _host.Launch(5, ExePath, new FakeMemory().WithVerifiedImage(profile), new ModuleImage(0x400000, 0x49B2000, ExePath));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick(); // attached before capture starts

        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        monitor.Tick(); // nothing changes (title screen)
        var zip = capture.Stop();

        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("recording-1.jsonl")!.Open());
        var (_, frames) = SessionRecording.Read(reader);
        Assert.Single(frames);
    }

    [Fact]
    public void Recordings_stay_compact_timer_only_changes_are_throttled_and_raw_values_omitted()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        var memory = new FakeMemory().WithVerifiedImage(profile);
        memory.WriteU8(profile.FindField("room")!.Address, 0x06)
            .WriteI16(profile.FindField("hp")!.Address, 96)
            .WriteU8(profile.FindField("maxHp")!.Address, 96);
        _host.Launch(6, ExePath, memory, new ModuleImage(0x400000, 0x49B2000, ExePath));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick();

        // 60 polls of normal play: the timer advances one frame per poll (2 s of game time).
        var timer = profile.FindField("gameTimer")!.Address;
        for (uint frame = 1; frame <= 60; frame++)
        {
            memory.WriteU32(timer, frame);
            monitor.Tick();
        }

        var zip = capture.Stop();
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("recording-1.jsonl")!.Open());
        var (_, frames) = SessionRecording.Read(reader);

        Assert.InRange(frames.Count, 2, 4); // first state plus roughly one per second, not 61
        Assert.All(frames, f => Assert.Empty(f.Snapshot.RawFields));
    }

    [Fact]
    public void Captures_include_the_game_memory_log()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        var memory = new FakeMemory().WithVerifiedImage(profile);
        foreach (var region in profile.CaptureRegions)
        {
            memory.Write(region.Address, new byte[region.Length]); // real process memory is fully mapped
        }

        var enemyType = profile.FindField("enemy0Type")!.Address;
        _host.Launch(7, ExePath, memory, new ModuleImage(0x400000, 0x49B2000, ExePath));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick();
        for (var i = 1; i <= 3; i++)
        {
            memory.WriteU8(enemyType, (byte)i);
            _time.Advance(TimeSpan.FromMilliseconds(150));
            monitor.Tick();
        }

        Assert.True(monitor.RecordRegions); // capture switched region recording on
        var zip = capture.Stop();
        Assert.False(monitor.RecordRegions); // and off again

        using var archive = ZipFile.OpenRead(zip);
        using var log = new MemoryStream();
        archive.GetEntry("memory-1.bin")!.Open().CopyTo(log);
        log.Position = 0;
        var (regions, records) = RegionLogReader.Open(log);
        Assert.Equal(profile.CaptureRegions.Select(r => r.Name), regions.Select(r => r.Name));
        var states = records.ToList();
        Assert.Equal(4, states.Count);
        var game = profile.CaptureRegions.Single(r => r.Name == "gameState");
        Assert.Equal(3, states[^1].State[0][(int)(enemyType - game.Address)]);
    }

    [Fact]
    public void Markers_record_the_moment_room_and_game_time_and_notes_can_be_added_later()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        var memory = new FakeMemory().WithVerifiedImage(profile);
        memory.WriteU8(profile.FindField("room")!.Address, 0x06)
            .WriteI16(profile.FindField("hp")!.Address, 96)
            .WriteU8(profile.FindField("maxHp")!.Address, 96)
            .WriteU32(profile.FindField("gameTimer")!.Address, 300);
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");

        var early = capture.AddMarker("before the game started");
        Assert.Equal((0, null, null), (early.Attachment, early.Room, early.GameSeconds));

        _host.Launch(9, ExePath, memory, new ModuleImage(0x400000, 0x49B2000, ExePath));
        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick();
        var hotkey = capture.AddMarker();
        Assert.Equal((2, 1, "106", 10.0, ""), (hotkey.Number, hotkey.Attachment, hotkey.Room, hotkey.GameSeconds, hotkey.Note));

        Assert.True(capture.SetMarkerNote(2, "  pushing the\nstatue  "));
        Assert.False(capture.SetMarkerNote(3, "no such marker"));
        Assert.Equal(CaptureSession.MaxMarkerNoteLength, capture.AddMarker(new string('x', 500)).Note.Length);
        var zip = capture.Stop();
        Assert.Throws<ObjectDisposedException>(() => capture.AddMarker("after stop"));

        using var archive = ZipFile.OpenRead(zip);
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var markers = Read("markers.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<CaptureMarker>(line, WebJson)!)
            .ToList();
        Assert.Equal(3, markers.Count);
        Assert.Equal("pushing the statue", markers[1].Note);
        Assert.Equal("106", markers[1].Room);
        Assert.Contains("Marker 2", Read("events-1.txt"), StringComparison.Ordinal);
        Assert.Equal(3, JsonSerializer.Deserialize<CaptureInfo>(Read("capture-info.json"))!.Markers);
    }

    [Fact]
    public void Ask_mode_answers_are_saved_with_the_changes_they_explain()
    {
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        var question = new Lockpick.Core.Insight.PendingQuestion(
            [new("flagScenarioB0:0x20000000:set", "story B flag #2 set (flagScenarioB0 bit 0x20000000) in room 104")],
            "104", TimeSpan.FromSeconds(87), _time.GetUtcNow());

        var answered = capture.AddAnswer(question, "  Kenneth's body\nfound ");
        var skipped = capture.AddAnswer(question, null);
        var auto = capture.AddAttribution(new Lockpick.Core.Insight.AttributedChange(question.Changes[0], "used Star crest", "11A", null, _time.GetUtcNow()));
        Assert.Equal(("auto", "used Star crest", "11A"), (auto.Kind, auto.Note, auto.Room));

        Assert.Equal(("answer", "Kenneth's body found", "104", 87.0), (answered.Kind, answered.Note, answered.Room, answered.GameSeconds));
        Assert.Equal("skipped", skipped.Kind);
        Assert.Contains("story B flag #2", Assert.Single(answered.Changes!), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"answer\"", File.ReadAllText(Path.Combine(capture.Directory, "markers.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_builds_are_fingerprinted_but_not_recorded()
    {
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(_adapter.Profiles[0], ExePath) with { Sha256 = new string('C', 64) };
        _host.Launch(7, ExePath, new FakeMemory(), new ModuleImage(0x400000, 0x49B2000, ExePath));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");

        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick();

        Assert.False(capture.IsRecordingGame);
        var info = JsonSerializer.Deserialize<CaptureInfo>(File.ReadAllText(Path.Combine(capture.Directory, "capture-info.json")))!;
        var build = Assert.Single(info.Builds);
        Assert.Null(build.ProfileId);
        Assert.Equal(new string('C', 64), build.Sha256);
    }
}
