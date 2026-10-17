namespace Lockpick.Core.Runs;

/// <summary>
/// The game install compared with the files its official release ships (for leaderboard runs, which forbid mods):
/// files changed or missing, and files beside the game the release doesn't have (mod loaders, plugins). Findings are
/// the adapter's other checks of the install (an allowed patch and its banned options, say).
/// </summary>
/// <param name="FilesCompared">False when the adapter has no list of this release's files (only the findings apply).</param>
public sealed record InstallCheck(
    string Release,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<InstallFinding>? Findings = null,
    bool FilesCompared = true)
{
    /// <summary>Every official file is there and unchanged (extra files are reported, but don't change shipped ones).</summary>
    public bool Unchanged => FilesCompared && Changed.Count == 0 && Missing.Count == 0;

    public IReadOnlyList<InstallFinding> Notes => Findings ?? [];
}

public enum InstallFindingLevel
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One check of the install beyond the file list, e.g. "Classic REbirth 1.1.4 (allowed)" or "quickturn is on".</summary>
public sealed record InstallFinding(InstallFindingLevel Level, string Text);
