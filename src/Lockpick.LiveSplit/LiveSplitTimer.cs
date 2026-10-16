using System.Runtime.InteropServices;
using Native = LiveSplitCore;

namespace Lockpick.LiveSplit;

/// <summary>Where livesplit-core's timer is (its own <c>TimerPhase</c>).</summary>
public enum LiveSplitPhase
{
    NotRunning = 0,
    Running = 1,
    Ended = 2,
    Paused = 3,
}

/// <summary>
/// A livesplit-core timer on game time, driven from outside (Lockpick's run): the times, PB, golds and history are
/// livesplit-core's, so they match LiveSplit exactly, and its layout can be drawn and its splits saved as `.lss`
/// without loss. Thread-safe: every call takes one lock (the run is driven from the monitor thread, drawn from the UI).
/// </summary>
public sealed class LiveSplitTimer : IDisposable
{
    /// <summary>livesplit-core's TimingMethod: game time.</summary>
    private const byte GameTime = 1;

    private readonly object _gate = new();
    private readonly byte _lang;
    private readonly Native.Timer _timer;
    private Native.Layout _layout;
    private readonly Native.ImageCache _images = new();
    private readonly Native.SoftwareRenderer _renderer = new();
    private bool _disposed;

    private LiveSplitTimer(Native.Run run)
    {
        _lang = Native.Lang.ParseLocale("en");
        _timer = Native.Timer.New(run) ?? throw new InvalidDataException("livesplit-core refused the splits (a run needs at least one split).");
        _timer.SetCurrentTimingMethod(GameTime);
        _layout = Native.Layout.DefaultLayout(_lang);
    }

    /// <summary>
    /// A timer for these splits: from <paramref name="lssPath"/> when it is a readable LiveSplit file (kept whole —
    /// icons, history, comparisons), else a new run named <paramref name="game"/> / <paramref name="category"/> with
    /// <paramref name="segments"/>.
    /// </summary>
    public static LiveSplitTimer Create(string? lssPath, string game, string category, IReadOnlyList<string> segments)
    {
        if (lssPath is not null && File.Exists(lssPath))
        {
            using var stream = File.OpenRead(lssPath);
            using var parsed = Native.Run.Parse(stream, Path.GetDirectoryName(lssPath) ?? string.Empty);
            if (parsed.ParsedSuccessfully())
            {
                var loaded = parsed.Unwrap();
                if (loaded.Len() == (ulong)segments.Count)
                {
                    return new LiveSplitTimer(loaded);
                }

                loaded.Dispose(); // another set of splits under the same name: start fresh
            }
        }

        var run = new Native.Run();
        run.SetGameName(game);
        run.SetCategoryName(category);
        foreach (var name in segments)
        {
            run.PushSegment(new Native.Segment(name));
        }

        return new LiveSplitTimer(run);
    }

    /// <summary>The native library is there (livesplit_core.dll next to Lockpick).</summary>
    public static bool Available
    {
        get
        {
            if (!NativeLibrary.TryLoad("livesplit_core", typeof(LiveSplitTimer).Assembly, null, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
    }

    public LiveSplitPhase Phase => Locked(() => (LiveSplitPhase)_timer.CurrentPhase());

    /// <summary>The split being run (−1 before the start, the split count after the end).</summary>
    public int CurrentSplitIndex => Locked(() => (int)_timer.CurrentSplitIndex());

    /// <summary>The comparisons the splits can be compared against (Personal Best, Best Segments, Average Segments, …
    /// and the file's own), in LiveSplit's order.</summary>
    public IReadOnlyList<string> Comparisons => Locked(() =>
    {
        var run = _timer.GetRun();
        var names = new List<string>((int)run.ComparisonsLen());
        for (ulong i = 0; i < run.ComparisonsLen(); i++)
        {
            names.Add(run.Comparison(i));
        }

        return names;
    });

    /// <summary>The comparison the layout shows and splits are judged against.</summary>
    public string CurrentComparison => Locked(() => _timer.CurrentComparison());

    /// <summary>Compares against <paramref name="comparison"/> (false when the splits have no such comparison).</summary>
    public bool SetComparison(string comparison) => Locked(() =>
    {
        _timer.SetCurrentComparison(comparison);
        return _timer.CurrentComparison() == comparison;
    });

    /// <summary>Each split's time in <paramref name="comparison"/> (game time from the start; null where it has none).</summary>
    public IReadOnlyList<TimeSpan?> SplitTimes(string comparison) => Locked(() =>
    {
        var run = _timer.GetRun();
        var times = new TimeSpan?[(int)run.Len()];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = Seconds(run.Segment((ulong)i).Comparison(comparison).GameTime());
        }

        return times;
    });

    /// <summary>Each segment's best time ever (game time; null where there is none).</summary>
    public IReadOnlyList<TimeSpan?> BestSegments() => Locked(() =>
    {
        var run = _timer.GetRun();
        var times = new TimeSpan?[(int)run.Len()];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = Seconds(run.Segment((ulong)i).BestSegmentTime().GameTime());
        }

        return times;
    });

    /// <summary>LiveSplit's sum of best segments (game time), null until every segment has a time.</summary>
    public TimeSpan? SumOfBest() => Locked(() =>
    {
        using var sum = Native.Analysis.CalculateSumOfBest(_timer.GetRun(), simpleCalculation: false, useCurrentRun: false, GameTime);
        return Seconds(sum);
    });

    /// <summary>A new attempt, the timer starting on game time with its own count paused so only
    /// <see cref="SetGameTime"/> moves it. The previous attempt goes into the history, or is dropped when
    /// <paramref name="keepPrevious"/> is false (an attempt that mustn't count: the trainer was on, from a save).</summary>
    public void Start(bool keepPrevious = true) => Locked(() =>
    {
        _timer.Reset(updateSplits: keepPrevious);
        _timer.Start();
        _timer.InitializeGameTime();
        _timer.PauseGameTime();
        SetGameTimeLocked(TimeSpan.Zero);
    });

    public void SetGameTime(TimeSpan time) => Locked(() => SetGameTimeLocked(time));

    /// <summary>Sets the game time, then splits (so the split carries exactly that time).</summary>
    public void Split(TimeSpan at) => Locked(() =>
    {
        SetGameTimeLocked(at);
        _timer.Split();
    });

    public void Undo() => Locked(() => _timer.UndoSplit());

    public void Skip() => Locked(() => _timer.SkipSplit());

    /// <summary>Ends the attempt; its times go into the history (PB and golds updated as LiveSplit does), or are
    /// dropped when <paramref name="keep"/> is false.</summary>
    public void Reset(bool keep = true) => Locked(() => _timer.Reset(updateSplits: keep));

    /// <summary>
    /// LiveSplit's default layout with, when given, one more line under it (e.g. "World record" / "38:01.0 Hamo").
    /// </summary>
    public void SetFooter(string? left, string? right) => Locked(() =>
    {
        var layout = Native.Layout.DefaultLayout(_lang);
        if (left is not null || right is not null)
        {
            using var text = new Native.TextComponent();
            text.SetLeft(left ?? string.Empty);
            text.SetRight(right ?? string.Empty);
            layout.Push(text.IntoGeneric());
        }

        _layout.Dispose();
        _layout = layout;
    });

    /// <summary>The splits as a LiveSplit file, with every attempt so far.</summary>
    public string SaveLss() => Locked(() => _timer.SaveAsLss());

    /// <summary>
    /// LiveSplit's default layout drawn by livesplit-core at <paramref name="width"/> × <paramref name="height"/>
    /// pixels, as premultiplied BGRA (what a layered window shows).
    /// </summary>
    public byte[] Render(int width, int height) => Locked(() =>
    {
        var pixels = new byte[width * height * 4];
        using (var state = _layout.State(_images, _timer, _lang))
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                // The stride is in pixels, not bytes (docs/LIVESPLIT_CORE.md).
                _renderer.Render(state, _images, handle.AddrOfPinnedObject(), (uint)width, (uint)height, (uint)width, true);
            }
            finally
            {
                handle.Free();
            }
        }

        ToPremultipliedBgra(pixels);
        return pixels;
    });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.Dispose();
            _images.Dispose();
            _layout.Dispose();
            _timer.Dispose();
        }
    }

    private static TimeSpan? Seconds(Native.TimeSpanRef? time) => time is null ? null : TimeSpan.FromSeconds(time.TotalSeconds());

    private void SetGameTimeLocked(TimeSpan time)
    {
        using var span = Native.TimeSpan.FromSeconds(Math.Max(0, time.TotalSeconds));
        _timer.SetGameTime(span);
    }

    private static void ToPremultipliedBgra(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var (r, g, b, a) = (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            rgba[i] = (byte)(b * a / 255);
            rgba[i + 1] = (byte)(g * a / 255);
            rgba[i + 2] = (byte)(r * a / 255);
        }
    }

    private T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action();
        }
    }

    private void Locked(Action action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action();
        }
    }
}
