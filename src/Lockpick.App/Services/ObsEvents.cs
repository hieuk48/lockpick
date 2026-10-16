using System.Globalization;
using System.Text.Json;
using Lockpick.Core.Display;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// What Lockpick's OBS script (obs/lockpick.lua, #66) reads, since OBS's Lua has no network: <c>connection.json</c>
/// (the API's address and token, so the script adds the browser sources without any copy-paste) and
/// <c>events.txt</c>, one line per moment — <c>time-ms TAB kind TAB detail</c> — for recording with runs, chapters,
/// the replay buffer and the "paused" scene. The events file starts empty each time Lockpick starts.
/// Kinds: run-start, split, run-end, finished, new-best, death, challenge-fail, game-active, game-inactive.
/// </summary>
public sealed class ObsEvents : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AppServices _services;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool? _gameActive;

    public ObsEvents(AppServices services, ILogger log)
    {
        _services = services;
        _log = log;
        Directory = Path.Combine(AppPaths.DataRoot, "obs");
        Write(truncate: true, null);
        WriteConnection();
        services.Runs.EventRaised += OnRunEvent;
        services.Runs.AttemptEnded += OnAttemptEnded;
        services.Runs.Died += OnDied;
        services.Challenges.EventRaised += OnChallengeEvent;
        _timer = new Timer(_ => CheckGame(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Where the files are (the script finds them in %LOCALAPPDATA%\Lockpick\obs).</summary>
    public string Directory { get; }

    /// <summary>The game's window as last seen (set by the app; null when there is none).</summary>
    public Func<GameWindowInfo?>? Game { get; set; }

    /// <summary>Writes connection.json again (the API was switched on or off, or its port or token changed).</summary>
    public void WriteConnection()
    {
        var settings = _services.Settings.Current;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var connection = new
            {
                api = settings.ApiEnabled,
                port = settings.ApiPort,
                token = settings.ApiEnabled ? settings.ApiToken : null,
                version = _services.AppVersion,
                events = Path.Combine(Directory, "events.txt"),
                livesplit = _services.LiveSplitEngine.Available,
            };
            File.WriteAllText(Path.Combine(Directory, "connection.json"), JsonSerializer.Serialize(connection, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The OBS connection file couldn't be written: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _services.Runs.EventRaised -= OnRunEvent;
        _services.Runs.AttemptEnded -= OnAttemptEnded;
        _services.Runs.Died -= OnDied;
        _services.Challenges.EventRaised -= OnChallengeEvent;
    }

    private void OnRunEvent(object? sender, GameEvent e)
    {
        switch (e)
        {
            case RunStarted started:
                Add("run-start", $"{started.SplitSet}, attempt {started.Attempt}");
                break;
            case SplitReached split:
                Add("split", split.Describe());
                break;
            case RunEnded ended:
                Add("run-end", ended.Describe());
                break;
        }
    }

    private void OnAttemptEnded(object? sender, RunRecord record)
    {
        if (record.Result != RunResult.Finished)
        {
            return;
        }

        var best = _services.RunLibrary.Find(record.SetId) is { } set ? _services.RunLibrary.Comparison(set).PersonalBest : [];
        var personalBest = record.IsClean && best.Count > 0 && best[^1] == record.GameTime;
        Add(personalBest ? "new-best" : "finished", $"{record.Category} in {record.GameTime:h\\:mm\\:ss\\.f}");
    }

    private void OnDied(object? sender, EventArgs e) => Add("death", $"death {_services.Runs.Deaths} this session");

    private void OnChallengeEvent(object? sender, GameEvent e)
    {
        if (e is ChallengeFailed failed)
        {
            Add("challenge-fail", $"{failed.Challenge}: {failed.Reason}");
        }
    }

    /// <summary>The game in front and showing, or not (minimized, or another window in front): for the "paused" scene.</summary>
    private void CheckGame()
    {
        if (Game?.Invoke() is not { } game)
        {
            _gameActive = null;
            return;
        }

        var active = game.Foreground && !game.Minimized && game.Visible;
        if (active != _gameActive)
        {
            _gameActive = active;
            Add(active ? "game-active" : "game-inactive", string.Empty);
        }
    }

    private void Add(string kind, string detail) => Write(truncate: false, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}\t{kind}\t{detail.Replace('\t', ' ').Replace('\n', ' ')}");

    private void Write(bool truncate, string? line)
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, "events.txt");
                if (truncate)
                {
                    File.WriteAllText(path, string.Empty);
                }

                if (line is not null)
                {
                    File.AppendAllText(path, line + "\n");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("The OBS events file couldn't be written: {Message}", ex.Message);
            }
        }
    }
}
