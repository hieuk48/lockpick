using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.Core.Adapters;

/// <summary>Result of matching an executable fingerprint against an adapter's known builds.</summary>
public sealed record ProfileMatch(VersionProfile? Profile, string Reason)
{
    public bool IsSupported => Profile is not null;

    public static ProfileMatch Supported(VersionProfile profile) => new(profile, $"Matched profile {profile.Id}");

    public static ProfileMatch Unsupported(string reason) => new(null, reason);
}

/// <summary>
/// Integration point for one game. Everything game-specific (process names, builds, memory layout,
/// interpretation of raw values) lives behind this interface; the core never sees offsets.
/// </summary>
public interface IGameAdapter
{
    /// <summary>Stable short identifier, e.g. "re1".</summary>
    string GameId { get; }

    string DisplayName { get; }

    /// <summary>Executable file names this game runs as (case-insensitive), e.g. "ResidentEvil.exe".</summary>
    IReadOnlyCollection<string> ExecutableNames { get; }

    IReadOnlyList<VersionProfile> Profiles { get; }

    /// <summary>Matches a fingerprint against known builds. Unknown builds must return <see cref="ProfileMatch.Unsupported"/>.</summary>
    ProfileMatch Identify(ExecutableFingerprint fingerprint);

    /// <summary>
    /// Verifies the running image against the profile (header, anchors) and creates a session.
    /// Throws <see cref="AttachRejectedException"/> when the process does not match.
    /// </summary>
    IGameSession Attach(IAttachedProcess process, VersionProfile profile);

    /// <summary>
    /// Finds changes the game's known data doesn't explain, for captures that ask the player about them; null when the
    /// adapter has no such knowledge.
    /// </summary>
    Insight.IChangeExplainer? CreateChangeExplainer(VersionProfile profile) => null;

    /// <summary>Works out the player's progress (items, documents, rooms, enemies, story); null when unsupported.</summary>
    Insight.IProgressTracker? CreateProgressTracker(VersionProfile profile) => null;

    /// <summary>
    /// Reads room layouts (collision, doors, items, cameras) from the player's own install at
    /// <paramref name="installDirectory"/>; null when the adapter can't.
    /// </summary>
    Maps.IRoomMapSource? CreateRoomMaps(string installDirectory) => null;
}

/// <summary>A verified connection to one running game process.</summary>
public interface IGameSession
{
    VersionProfile Profile { get; }

    /// <summary>When true, <see cref="Read"/> also copies the profile's capture regions into the snapshot.</summary>
    bool CaptureRegions { get; set; }

    /// <summary>Reads the current state. Must not throw for ordinary read failures; failed fields are marked instead.</summary>
    GameSnapshot Read(long sequence, DateTimeOffset now);
}

public sealed class AttachRejectedException(string message) : Exception(message);
