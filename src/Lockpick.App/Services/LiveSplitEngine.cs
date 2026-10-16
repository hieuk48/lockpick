using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Runs;
using Lockpick.LiveSplit;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// livesplit-core inside Lockpick (docs/LIVESPLIT_CORE.md): a LiveSplit timer that follows Lockpick's run — a new
/// attempt starts it on game time, every split, undo, skip and reset is mirrored, and its game time is set from the
/// game's clock — so the times, PB, golds and history are LiveSplit's own and its layout can be drawn on the overlay and
/// the OBS page. Each split set's LiveSplit file is kept in the runs folder (<c>&lt;set&gt;.lss</c>), whole; an imported
/// set starts from the player's file (which Lockpick never writes to). Off when the native library is missing.
/// </summary>
public sealed class LiveSplitEngine : IDisposable
{
    private static readonly TimeSpan GameTimeEvery = TimeSpan.FromMilliseconds(100);

    private readonly RunService _runs;
    private readonly IGameStateSource _source;
    private readonly string _directory;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private LiveSplitTimer? _timer;
    private string? _setId;
    private int _current;
    private DateTimeOffset _lastGameTime;
    private string? _record;

    public LiveSplitEngine(RunService runs, IGameStateSource source, string directory, ILogger log)
    {
        _runs = runs;
        _source = source;
        _directory = directory;
        _log = log;
        Available = LiveSplitTimer.Available;
        if (!Available)
        {
            _log.LogWarning("livesplit_core.dll is missing: the LiveSplit timer is off");
            return;
        }

        runs.StatusChanged += OnStatus;
        runs.EventRaised += OnEvent;
        source.Polled += OnPolled;
        OnStatus(this, runs.Status);
    }

    /// <summary>The native library loaded; the LiveSplit timer works.</summary>
    public bool Available { get; }

    /// <summary>Raised when the picture may have changed (a split, a new attempt, other splits).</summary>
    public event EventHandler? Changed;

    /// <summary>The timer is counting (the overlay redraws it every tick then).</summary>
    public bool Running => _timer?.Phase == LiveSplitPhase.Running;

    /// <summary>LiveSplit's layout for the chosen splits, premultiplied BGRA, or null when there is nothing to draw.</summary>
    public byte[]? Render(int width, int height)
    {
        lock (_gate)
        {
            return _timer?.Render(width, height);
        }
    }

    /// <summary>The world record line under LiveSplit's layout ("38:01.0 Hamo"), or null for none.</summary>
    public void SetRecord(string? record)
    {
        lock (_gate)
        {
            _record = record;
            _timer?.SetFooter(record is null ? null : "World record", record);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The chosen splits as a LiveSplit file with every attempt (LiveSplit's own format), or null.</summary>
    public string? SaveLss()
    {
        lock (_gate)
        {
            return _timer?.SaveLss();
        }
    }

    public void Dispose()
    {
        if (!Available)
        {
            return;
        }

        _runs.StatusChanged -= OnStatus;
        _runs.EventRaised -= OnEvent;
        _source.Polled -= OnPolled;
        lock (_gate)
        {
            // An attempt still on the clock (or finished but not yet reset) goes into the history before saving.
            if (_timer is { Phase: not LiveSplitPhase.NotRunning } timer)
            {
                timer.Reset();
            }

            Save();
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnStatus(object? sender, RunStatus status)
    {
        lock (_gate)
        {
            if (status.Set?.Id != _setId)
            {
                Save();
                _timer?.Dispose();
                _timer = null;
                _setId = status.Set?.Id;
                if (status.Set is { } set)
                {
                    try
                    {
                        var kept = PathFor(set);
                        _timer = LiveSplitTimer.Create(File.Exists(kept) ? kept : set.ImportedFrom, "Resident Evil", set.Category,
                            [.. set.Splits.Select(s => s.Name)]);
                        if (_record is not null)
                        {
                            _timer.SetFooter("World record", _record);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        _log.LogWarning("The LiveSplit timer couldn't load {Set}: {Message}", set.Id, ex.Message);
                    }
                }

                _current = status.Current;
            }
            else if (_timer is { } timer && status.State == RunState.Running)
            {
                // Undo and skip move the split index without a split event.
                if (status.Current < _current)
                {
                    for (var i = status.Current; i < _current; i++)
                    {
                        timer.Undo();
                    }
                }
                else if (status.Current > _current && status.Times.ElementAtOrDefault(status.Current - 1) is null)
                {
                    timer.Skip();
                }

                _current = status.Current;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEvent(object? sender, GameEvent e)
    {
        lock (_gate)
        {
            if (_timer is not { } timer)
            {
                return;
            }

            switch (e)
            {
                case RunStarted:
                    timer.Start();
                    _current = 0;
                    break;
                case SplitReached split:
                    timer.Split(split.GameTime);
                    _current = split.Index + 1;
                    break;
                case RunEnded ended:
                    if (ended.Result != RunResult.Finished)
                    {
                        timer.Reset();
                    }

                    Save();
                    break;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPolled(object? sender, Core.State.GameSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastGameTime < GameTimeEvery || _runs.Status.State != RunState.Running)
        {
            return;
        }

        _lastGameTime = now;
        lock (_gate)
        {
            if (_timer is { } timer && _runs.Status.GameTime(snapshot) is { } time)
            {
                timer.SetGameTime(time);
            }
        }
    }

    private void Save()
    {
        if (_timer is null || _setId is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, Safe(_setId) + ".lss");
            File.WriteAllText(path + ".tmp", _timer.SaveLss());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The LiveSplit file for {Set} couldn't be saved: {Message}", _setId, ex.Message);
        }
    }

    private string PathFor(SplitSet set) => Path.Combine(_directory, Safe(set.Id) + ".lss");

    private static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
