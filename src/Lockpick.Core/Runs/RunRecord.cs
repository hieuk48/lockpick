using System.Text.Json;
using System.Text.Json.Serialization;
using Lockpick.Core.State;

namespace Lockpick.Core.Runs;

/// <summary>One split of a recorded attempt: its name and when it was reached (null when it wasn't).</summary>
public sealed record RecordedSplit(string Name, TimeSpan? GameTime, TimeSpan? RealTime);

/// <summary>
/// One attempt, kept on the player's PC: the split set, how it went, every split's time and what could matter to a
/// leaderboard (started from a save, the trainer on or used). The in-game time is the game's own clock.
/// </summary>
public sealed record RunRecord(
    string SetId,
    string SetName,
    string Category,
    PlayableCharacter Character,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RunResult Result,
    TimeSpan GameTime,
    TimeSpan RealTime,
    IReadOnlyList<RecordedSplit> Splits,
    bool FromSave,
    int DamageTaken,
    bool CheatsUsed,
    string? Profile,
    string? GameSha256 = null)
{
    /// <summary>Counts for a personal best: finished from a new game, every split timed, no trainer.</summary>
    [JsonIgnore]
    public bool IsClean => Result == RunResult.Finished && !FromSave && !CheatsUsed && Splits.All(s => s.GameTime is not null);

    public static RunRecord From(RunStatus status, string? profile, string? gameSha256 = null)
    {
        var set = status.Set!;
        var splits = set.Splits.Select((s, i) => new RecordedSplit(s.Name, status.Times[i]?.GameTime, status.Times[i]?.RealTime)).ToList();
        return new RunRecord(set.Id, set.Name, set.Category, set.Character, status.Attempt, status.StartedAt ?? DateTimeOffset.MinValue,
            status.EndedAt ?? DateTimeOffset.MinValue, status.Result ?? RunResult.Reset, status.EndGameTime ?? TimeSpan.Zero,
            (status.EndedAt ?? DateTimeOffset.MinValue) - (status.StartedAt ?? DateTimeOffset.MinValue), splits, status.FromSave,
            status.DamageTaken, status.CheatsUsed, profile, gameSha256);
    }

    /// <summary>
    /// The attempt in a few lines for a leaderboard submission or a challenge post: category, result and in-game time,
    /// every split, and what timed it (Lockpick's version, the game build and its executable's hash, the trainer).
    /// </summary>
    public string Summary(string gameName, string timedBy)
    {
        static string Clock(TimeSpan t) => t.TotalHours >= 1
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}");
        var result = Result switch
        {
            RunResult.Finished => $"finished in {Clock(GameTime)} in-game time (the game's own clock), {Clock(RealTime)} real time",
            RunResult.Died => $"died at {Clock(GameTime)}",
            RunResult.QuitToTitle => $"quit to the title at {Clock(GameTime)}",
            RunResult.GameClosed => $"the game closed at {Clock(GameTime)}",
            RunResult.LockpickClosed => $"Lockpick was closed at {Clock(GameTime)}",
            _ => $"reset at {Clock(GameTime)}",
        };
        var lines = new List<string>
        {
            $"{gameName} — {Category} — {result}",
            "Splits: " + string.Join(" · ", Splits.Select(s => $"{s.Name} {(s.GameTime is { } t ? Clock(t) : "—")}")),
            $"Attempt {Attempt}, started {StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" + (FromSave ? ", from a save" : ", from a new game")
                + $"; damage taken {DamageTaken}",
            $"Timed by {timedBy}" + (Profile is null ? string.Empty : $" on {Profile}") + (GameSha256 is null ? string.Empty : $", ResidentEvil.exe SHA-256 {GameSha256}")
                + (CheatsUsed ? "; Lockpick's trainer was on or used during this attempt" : "; read-only, the trainer was off"),
        };
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// What a run is compared with: the personal best's split times (cumulative in-game time) and the best time of each
/// segment ever, from clean attempts. Either can come from an imported LiveSplit file instead.
/// </summary>
public sealed record RunComparison(IReadOnlyList<TimeSpan?> PersonalBest, IReadOnlyList<TimeSpan?> BestSegments)
{
    public static RunComparison None(int count) => new(new TimeSpan?[count], new TimeSpan?[count]);

    /// <summary>The fastest clean finished attempt, and the best of each segment over every attempt that timed both ends.</summary>
    public static RunComparison From(IEnumerable<RunRecord> records, int count)
    {
        var all = records.Where(r => r.Splits.Count == count && !r.CheatsUsed && !r.FromSave).ToList();
        var best = all.Where(r => r.IsClean).OrderBy(r => r.GameTime).FirstOrDefault();
        var personalBest = best?.Splits.Select(s => s.GameTime).ToArray() ?? new TimeSpan?[count];
        var segments = new TimeSpan?[count];
        foreach (var record in all)
        {
            for (var i = 0; i < count; i++)
            {
                var end = record.Splits[i].GameTime;
                var start = i == 0 ? TimeSpan.Zero : record.Splits[i - 1].GameTime;
                if (end is { } e && start is { } s && e >= s && (segments[i] is not { } known || e - s < known))
                {
                    segments[i] = e - s;
                }
            }
        }

        return new RunComparison(personalBest, segments);
    }

    /// <summary>Fills what this comparison lacks from <paramref name="other"/> (e.g. a LiveSplit file's PB before the
    /// player has one of their own here).</summary>
    public RunComparison Or(RunComparison other) => new(
        PersonalBest.All(t => t is null) ? other.PersonalBest : PersonalBest,
        [.. BestSegments.Select((t, i) => t is { } mine && other.BestSegments.ElementAtOrDefault(i) is { } theirs ? (mine < theirs ? mine : theirs) : t ?? other.BestSegments.ElementAtOrDefault(i))]);

    /// <summary>The sum of best segments (the best possible time), when every segment has one.</summary>
    public TimeSpan? SumOfBest => BestSegments.All(t => t is not null) ? BestSegments.Aggregate(TimeSpan.Zero, (a, t) => a + t!.Value) : null;
}

/// <summary>Every attempt, one JSON line each, in a file per split set under <paramref name="directory"/>.</summary>
public sealed class RunHistoryStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();

    public string Directory { get; } = directory;

    public void Add(RunRecord record)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(PathFor(record.SetId), JsonSerializer.Serialize(record, Options) + "\n");
        }
    }

    /// <summary>The attempts against <paramref name="setId"/>, oldest first (unreadable lines are skipped).</summary>
    public IReadOnlyList<RunRecord> Load(string setId)
    {
        lock (_gate)
        {
            var path = PathFor(setId);
            if (!File.Exists(path))
            {
                return [];
            }

            var records = new List<RunRecord>();
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<RunRecord>(line, Options) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A torn line (e.g. the PC lost power mid-write) loses that attempt only.
                }
            }

            return records;
        }
    }

    private string PathFor(string setId)
    {
        var safe = string.Concat(setId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(Directory, safe + ".jsonl");
    }
}
