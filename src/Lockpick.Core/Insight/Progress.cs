using Lockpick.Core.State;

namespace Lockpick.Core.Insight;

/// <summary>One thing to collect, explore or defeat, and whether the player has.</summary>
/// <param name="Room">Where it is (room id), so the player can find what's left.</param>
public sealed record ProgressEntry(string Name, string Room, bool Done);

/// <summary>A kind of progress within an area: "Items", "Documents and maps", "Rooms explored", "Enemies".</summary>
public sealed record ProgressGroup(string Title, IReadOnlyList<ProgressEntry> Entries)
{
    public int Done => Entries.Count(e => e.Done);

    public int Total => Entries.Count;
}

public sealed record AreaProgress(string Area, IReadOnlyList<ProgressGroup> Groups);

/// <summary>Where the player stands: per area, and the story so far.</summary>
/// <param name="Story">Named story and puzzle events that have happened, in the order the game data lists them.</param>
/// <param name="UnnamedStory">Story flags that are set but have no name yet.</param>
public sealed record ProgressReport(IReadOnlyList<AreaProgress> Areas, IReadOnlyList<string> Story, int UnnamedStory);

/// <summary>Works out the player's progress from one snapshot (flags, not history, so it also works from a loaded save).</summary>
public interface IProgressTracker
{
    /// <summary>Null when the snapshot isn't of a game in session.</summary>
    ProgressReport? Evaluate(GameSnapshot snapshot);
}
