namespace Lockpick.Core.Runs;

/// <summary>
/// The game install compared with the files its official release ships (for leaderboard runs, which forbid mods):
/// files changed or missing, and files beside the game the release doesn't have (mod loaders, plugins).
/// </summary>
public sealed record InstallCheck(string Release, IReadOnlyList<string> Changed, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    /// <summary>Every official file is there and unchanged (extra files are reported, but don't change shipped ones).</summary>
    public bool Unchanged => Changed.Count == 0 && Missing.Count == 0;
}
