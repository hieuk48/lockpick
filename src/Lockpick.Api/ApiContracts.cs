using System.Security.Cryptography;
using Lockpick.Core.Adapters;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Trainer;

namespace Lockpick.Api;

/// <summary>How the API listens and what it allows. Off unless the player turns it on.</summary>
public sealed record ApiOptions
{
    public const int DefaultPort = 1996;

    /// <summary>Listen on every network interface (other devices on the network) instead of this PC only.</summary>
    public bool AllowNetwork { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>Every request must carry it (header or <c>?token=</c>).</summary>
    public required string Token { get; init; }

    /// <summary>Allow trainer commands (still only while the player has Trainer Mode on).</summary>
    public bool AllowTrainer { get; init; }

    /// <summary>A new random token (URL-safe, 256 bits).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Capture control, provided by the app (captures belong to its Debug page).</summary>
public interface ICaptureControl
{
    bool IsCapturing { get; }

    /// <summary>Starts a capture; returns null on success or why it couldn't start.</summary>
    Task<string?> StartAsync();

    /// <summary>Stops and saves the capture; returns the saved file, or null when none was running.</summary>
    Task<string?> StopAsync();

    /// <summary>Adds a marker to the running capture; returns its number, or null when no capture is running.</summary>
    Task<int?> MarkAsync(string? note);
}

/// <summary>What the API reads and drives. Everything game-specific comes through the adapter.</summary>
public sealed record ApiHost(
    IGameStateSource Source,
    IGameAdapter Adapter,
    TrainerService? Trainer,
    ICaptureControl? Capture,
    string AppVersion,
    Lockpick.Core.Challenges.ChallengeService? Challenges = null,
    Lockpick.Core.Runs.RunService? Runs = null,
    Func<Lockpick.Core.Runs.SplitSet, Lockpick.Core.Runs.RunComparison>? Comparison = null,
    Lockpick.Core.Insight.EventFeed? Feed = null);
