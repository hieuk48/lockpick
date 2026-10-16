using Lockpick.Api;
using Lockpick.App.Services;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.LiveSplit;
using Lockpick.Tests.Core;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.App;

/// <summary>livesplit-core (native, built from the submodule) as Lockpick's timer engine (docs/LIVESPLIT_CORE.md).</summary>
public sealed class LiveSplitCoreTests : IDisposable
{
    private static readonly string[] Names = ["King Henry", "Yawn 1", "Mansion 1", "End"];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-lsc-" + Guid.NewGuid().ToString("N"));

    public LiveSplitCoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_native_library_is_next_to_Lockpick() => Assert.True(LiveSplitTimer.Available);

    [Fact]
    public void It_times_on_the_game_clock_lockpick_gives_it()
    {
        using var timer = LiveSplitTimer.Create(null, "Resident Evil", "Jill - Test", Names);
        Assert.Equal(LiveSplitPhase.NotRunning, timer.Phase);
        timer.Start();
        timer.Split(TimeSpan.FromSeconds(20.9));
        timer.Split(TimeSpan.FromSeconds(75.25));
        timer.SetGameTime(TimeSpan.FromSeconds(90));
        Assert.Equal(LiveSplitPhase.Running, timer.Phase);
        Assert.Equal(2, timer.CurrentSplitIndex);
        timer.Undo();
        Assert.Equal(1, timer.CurrentSplitIndex);
        timer.Skip();
        Assert.Equal(2, timer.CurrentSplitIndex);
    }

    [Fact]
    public void Its_layout_is_drawn_as_premultiplied_pixels()
    {
        using var timer = LiveSplitTimer.Create(null, "Resident Evil", "Jill - Test", Names);
        timer.Start();
        var pixels = timer.Render(170, 300);
        Assert.Equal(170 * 300 * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3] && pixels[i + 2] <= pixels[i + 3]); // premultiplied
        }
    }

    [Fact]
    public void A_saved_file_comes_back_with_its_history()
    {
        var path = Path.Combine(_folder, "set.lss");
        using (var timer = LiveSplitTimer.Create(null, "Resident Evil", "Jill - Test", Names))
        {
            timer.Start();
            timer.Split(TimeSpan.FromSeconds(20));
            timer.Reset();
            File.WriteAllText(path, timer.SaveLss());
        }

        var saved = File.ReadAllText(path);
        Assert.Contains("<CategoryName>Jill - Test</CategoryName>", saved, StringComparison.Ordinal);
        Assert.Contains("<AttemptCount>1</AttemptCount>", saved, StringComparison.Ordinal);
        using var again = LiveSplitTimer.Create(path, "Resident Evil", "Jill - Test", Names);
        Assert.Contains("<AttemptCount>1</AttemptCount>", again.SaveLss(), StringComparison.Ordinal);
        using var other = LiveSplitTimer.Create(path, "Resident Evil", "Jill - Test", ["Only one"]); // other splits: a fresh run
        Assert.Contains("<AttemptCount>0</AttemptCount>", other.SaveLss(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_follows_lockpick_s_run_and_keeps_its_file()
    {
        var source = new FakeStateSource();
        source.Attach(1, TrainerServiceTests.Profile);
        using var runs = new RunService(source, null, _ => null);
        var set = new SplitSet("t", "Test", "Jill - Test", PlayableCharacter.Jill, [.. Names.Select(n => new SplitDefinition(n, new ManualSplit()))], false, "test");
        using (var engine = new LiveSplitEngine(runs, source, _folder, NullLogger.Instance))
        {
            runs.Choose(set);
            source.Poll(new GameSnapshot { InGameplay = Field<bool>.Valid(true), GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(1)) });
            source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), IsNewGame: true));
            Assert.True(engine.Running);
            runs.Split();
            runs.Undo();
            runs.Split();
            runs.Reset(); // the attempt ends: into livesplit-core's history and the file
            Assert.False(engine.Running);
            Assert.NotNull(engine.Render(170, 300));
        }

        var kept = File.ReadAllText(Path.Combine(_folder, "t.lss"));
        Assert.Contains("<AttemptCount>1</AttemptCount>", kept, StringComparison.Ordinal);
        Assert.Contains("<Name>King Henry</Name>", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparisons_best_segments_and_the_sum_of_best_come_from_the_history()
    {
        using var timer = LiveSplitTimer.Create(null, "Resident Evil", "Jill - Test", Names);
        timer.Start();
        foreach (var at in new[] { 20.0, 80, 140, 200 })
        {
            timer.Split(TimeSpan.FromSeconds(at));
        }

        timer.Start(); // the first attempt (the PB) goes into the history
        foreach (var at in new[] { 25.0, 75, 150, 215 })
        {
            timer.Split(TimeSpan.FromSeconds(at));
        }

        timer.Reset();

        Assert.Contains("Personal Best", timer.Comparisons);
        Assert.Contains("Best Segments", timer.Comparisons);
        Assert.Contains("Average Segments", timer.Comparisons);
        Assert.Equal([20.0, 80, 140, 200], timer.SplitTimes("Personal Best").Select(t => t!.Value.TotalSeconds));
        Assert.Equal([20.0, 50, 60, 60], timer.BestSegments().Select(t => t!.Value.TotalSeconds)); // 50: the second attempt's Yawn 1
        Assert.Equal(190, timer.SumOfBest()!.Value.TotalSeconds, precision: 3);
        Assert.True(timer.SetComparison("Best Segments"));
        Assert.Equal("Best Segments", timer.CurrentComparison);
        Assert.False(timer.SetComparison("No such comparison"));
        Assert.Equal("Best Segments", timer.CurrentComparison);
    }

    [Fact]
    public void An_attempt_that_doesn_t_count_is_dropped()
    {
        using var timer = LiveSplitTimer.Create(null, "Resident Evil", "Jill - Test", Names);
        timer.Start();
        timer.Split(TimeSpan.FromSeconds(20));
        timer.Reset(keep: false);
        // LiveSplit counts every start, but the attempt leaves no history, times or golds.
        Assert.Contains("<AttemptHistory/>", timer.SaveLss(), StringComparison.Ordinal);
        Assert.Null(timer.BestSegments()[0]);
    }

    [Fact]
    public void The_engine_keeps_attempts_from_a_save_out_of_livesplit_s_history_and_compares_as_chosen()
    {
        var source = new FakeStateSource();
        source.Attach(1, TrainerServiceTests.Profile);
        using var runs = new RunService(source, null, _ => null) { StartOnLoad = true };
        var set = new SplitSet("t", "Test", "Jill - Test", PlayableCharacter.Jill, [.. Names.Select(n => new SplitDefinition(n, new ManualSplit()))], false, "test");
        using (var engine = new LiveSplitEngine(runs, source, _folder, NullLogger.Instance, "Best Segments"))
        {
            runs.Choose(set);
            source.Poll(new GameSnapshot { InGameplay = Field<bool>.Valid(true), GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(1)) });
            source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), IsNewGame: false)); // practice from a save
            Assert.True(engine.Running);
            runs.Split();
            runs.Reset();
            Assert.Equal("Best Segments", engine.Comparison);
            var own = RunComparison.None(Names.Length);
            Assert.Same(own, engine.Against(set, own)); // nothing in Best Segments yet: no times to take, keep Lockpick's
        }

        var kept = File.ReadAllText(Path.Combine(_folder, "t.lss"));
        Assert.Contains("<AttemptHistory/>", kept, StringComparison.Ordinal);
        Assert.Contains("<BestSegmentTime/>", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void The_time_to_gain_is_the_personal_best_against_the_best_segments()
    {
        var set = new SplitSet("t", "Test", "Jill - Test", PlayableCharacter.Jill, [.. Names.Select(n => new SplitDefinition(n, new ManualSplit()))], false, "test");
        TimeSpan? S(double seconds) => TimeSpan.FromSeconds(seconds);
        var comparison = new RunComparison([S(20), S(80), S(140), S(200)], [S(20), S(50), S(60), S(58)]);
        var rows = Lockpick.App.ViewModels.RunRows.TimeSaves(set, comparison);
        Assert.Equal(["Yawn 1", "End"], rows.Select(r => r.Name)); // 10 s and 2 s to gain; the others are already best
        Assert.Equal("−10.0", rows[0].Gain);
        Assert.Equal("1:00.0", rows[0].PersonalBest);
    }

    [Fact]
    public void Pictures_are_written_as_png()
    {
        byte[] bgra = [0, 0, 128, 128, 255, 255, 255, 255]; // half-transparent red, opaque white
        var png = Png.FromPremultipliedBgra(bgra, 2, 1);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }
}
