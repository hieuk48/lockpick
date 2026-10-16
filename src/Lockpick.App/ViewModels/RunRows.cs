using System.Globalization;
using Lockpick.Core.Runs;

namespace Lockpick.App.ViewModels;

/// <summary>How a split went against the comparison: ahead, behind, or the best that segment has ever been.</summary>
public enum SplitMark
{
    None,
    Ahead,
    Behind,
    Gold,
}

/// <summary>One split as the Runs page and the overlay show it.</summary>
public sealed record SplitRowData(int Index, string Name, string Trigger, string Time, string Delta, SplitMark Mark, bool IsCurrent, bool IsDone)
{
    public bool IsAhead => Mark == SplitMark.Ahead;

    public bool IsBehind => Mark == SplitMark.Behind;

    public bool IsGold => Mark == SplitMark.Gold;
}

/// <summary>Works out the split rows (times, differences to the best, gold segments) the way LiveSplit shows them.</summary>
public static class RunRows
{
    /// <param name="status">The run (or the chosen set, when no attempt is running).</param>
    /// <param name="comparison">The personal best and best segments.</param>
    /// <param name="now">The run's in-game time now, for the current split's live difference.</param>
    public static IReadOnlyList<SplitRowData> Build(RunStatus status, RunComparison comparison, TimeSpan? now)
    {
        if (status.Set is not { } set)
        {
            return [];
        }

        var running = status.State == RunState.Running;
        var attempt = status.State is RunState.Running or RunState.Finished or RunState.Ended;
        var rows = new List<SplitRowData>(set.Splits.Count);
        for (var i = 0; i < set.Splits.Count; i++)
        {
            var split = set.Splits[i];
            var best = comparison.PersonalBest.ElementAtOrDefault(i);
            var time = attempt ? status.Times.ElementAtOrDefault(i) : null;
            var current = running && i == status.Current;
            if (time is not null)
            {
                var previous = i == 0 ? TimeSpan.Zero : status.Times[i - 1]?.GameTime;
                var segment = previous is { } p ? time.GameTime - p : (TimeSpan?)null;
                var mark = segment is { } s && comparison.BestSegments.ElementAtOrDefault(i) is { } gold && s < gold ? SplitMark.Gold
                    : best is { } b ? (time.GameTime <= b ? SplitMark.Ahead : SplitMark.Behind)
                    : SplitMark.None;
                rows.Add(new SplitRowData(i, split.Name, split.Trigger.Describe(), Clock(time.GameTime),
                    best is { } pb ? Delta(time.GameTime - pb) : string.Empty, mark, false, true));
            }
            else
            {
                var behind = current && now is { } t && best is { } pb && t > pb ? Delta(t - pb) : string.Empty;
                var skipped = attempt && i < status.Current;
                rows.Add(new SplitRowData(i, split.Name, split.Trigger.Describe(),
                    skipped ? "—" : best is { } b ? Clock(b) : "—", behind, behind.Length > 0 ? SplitMark.Behind : SplitMark.None, current, skipped));
            }
        }

        return rows;
    }

    /// <summary>"1:02:03.4", "2:03.4" under an hour.</summary>
    public static string Clock(TimeSpan time)
    {
        var tenths = (int)(time.Ticks / TimeSpan.TicksPerMillisecond % 1000 / 100);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}.{tenths}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:D2}.{tenths}");
    }

    /// <summary>"+1:02.3" behind, "−0:05.1" ahead.</summary>
    public static string Delta(TimeSpan difference)
    {
        var sign = difference < TimeSpan.Zero ? "−" : "+";
        var size = difference.Duration();
        var tenths = (int)(size.Ticks / TimeSpan.TicksPerMillisecond % 1000 / 100);
        return size.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)size.TotalMinutes}:{size.Seconds:D2}.{tenths}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{size.Seconds}.{tenths}");
    }
}
