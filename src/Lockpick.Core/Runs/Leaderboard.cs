namespace Lockpick.Core.Runs;

/// <summary>One choice of a leaderboard filter (e.g. Jill), with the board's own id for it.</summary>
public sealed record LeaderboardChoice(string Key, string Label, string Id)
{
    public override string ToString() => Label;
}

/// <summary>A filter of the game's leaderboard (a speedrun.com subcategory variable, e.g. Character).</summary>
public sealed record LeaderboardFilter(string Key, string Label, string VariableId, IReadOnlyList<LeaderboardChoice> Choices);

/// <summary>
/// Where the game's runs are ranked on speedrun.com: its game, category and platform ids, the filters that pick one
/// board (subcategories), and which of the board's times holds the run's time.
/// </summary>
public sealed record LeaderboardDefinition(
    string Game,
    string Category,
    string Platform,
    string TimeField,
    string Weblink,
    IReadOnlyList<LeaderboardFilter> Filters);

/// <summary>A run on the board.</summary>
public sealed record LeaderboardEntry(int Place, string Runner, string? RunnerId, TimeSpan Time, DateOnly? Date, string? Link);

/// <summary>One board as read from speedrun.com, best first.</summary>
public sealed record LeaderboardResult(IReadOnlyList<LeaderboardEntry> Entries, string? Weblink, DateTimeOffset FetchedAt)
{
    public LeaderboardEntry? Record => Entries.Count > 0 ? Entries[0] : null;

    /// <summary>The place a run of <paramref name="time"/> would take (ties share a place, as on the board).</summary>
    public int PlaceFor(TimeSpan time) => Entries.Count(e => e.Time < time) + 1;

    /// <summary>The runner's best run on this board, if they have one.</summary>
    public LeaderboardEntry? Best(string? runnerId, string? runnerName) =>
        Entries.FirstOrDefault(e => (runnerId is not null && e.RunnerId == runnerId)
            || (runnerName is not null && string.Equals(e.Runner, runnerName, StringComparison.OrdinalIgnoreCase)));
}
