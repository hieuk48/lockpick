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
/// set starts from the player's file (which Lockpick never writes to). An attempt that doesn't count for Lockpick
/// (the trainer was on, or it began from a save) is dropped, not kept in the history, so it can't become a PB or a
/// gold. The player picks the comparison (Runs page); Personal Best is the default. Off when the native library is
/// missing.
/// </summary>
public sealed class LiveSplitEngine : IDisposable
{
    /// <summary>LiveSplit's own comparison names.</summary>
    public const string PersonalBest = "Personal Best";

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
    private string _comparison;
    private bool _lastCounts = true;

    public LiveSplitEngine(RunService runs, IGameStateSource source, string directory, ILogger log, string? comparison = null)
    {
        _runs = runs;
        _source = source;
        _directory = directory;
        _log = log;
        _comparison = string.IsNullOrEmpty(comparison) ? PersonalBest : comparison;
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

    /// <summary>The comparisons the chosen splits offer (LiveSplit's: Personal Best, Best Segments, Average
    /// Segments, …, and the file's own); empty without a timer.</summary>
    public IReadOnlyList<string> Comparisons
    {
        get
        {
            lock (_gate)
            {
                return _timer?.Comparisons ?? [];
            }
        }
    }

    /// <summary>The comparison chosen (it applies to every split set that has it, else Personal Best).</summary>
    public string Comparison
    {
        get
        {
            lock (_gate)
            {
                return _comparison;
            }
        }
    }

    public void SetComparison(string comparison)
    {
        lock (_gate)
        {
            _comparison = comparison;
            _timer?.SetComparison(comparison);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What <paramref name="set"/>'s splits are judged against: Lockpick's own (<paramref name="own"/>: its personal
    /// best and best segments) for Personal Best, else the chosen comparison's split times from LiveSplit's history,
    /// with the best segments kept.
    /// </summary>
    public RunComparison Against(SplitSet set, RunComparison own)
    {
        lock (_gate)
        {
            if (_timer is not { } timer || _setId != set.Id || _comparison == PersonalBest || timer.CurrentComparison != _comparison)
            {
                return own;
            }

            var times = timer.SplitTimes(_comparison);
            return times.Count == set.Splits.Count && times.Any(t => t is not null) ? own with { PersonalBest = times } : own;
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
                timer.Reset(keep: _runs.Status.State == RunState.Running ? Counts(_runs.Status) : _lastCounts);
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
                if (_timer is { Phase: not LiveSplitPhase.NotRunning } previous)
                {
                    previous.Reset(keep: _lastCounts); // a finished attempt goes into its file's history first
                    _lastCounts = true;
                }

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

                        _timer.SetComparison(_comparison);
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
                else
                {
                    // Skips (one, or several when an attempt from a save starts further on) leave no time.
                    for (var i = _current; i < status.Current; i++)
                    {
                        if (status.Times.ElementAtOrDefault(i) is null)
                        {
                            timer.Skip();
                        }
                    }
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
                    timer.Start(keepPrevious: _lastCounts);
                    _lastCounts = true;
                    _current = 0;
                    break;
                case SplitReached split:
                    timer.Split(split.GameTime);
                    _current = split.Index + 1;
                    break;
                case RunEnded ended:
                    _lastCounts = Counts(_runs.Status);
                    if (ended.Result != RunResult.Finished)
                    {
                        timer.Reset(keep: _lastCounts);
                        _lastCounts = true;
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

    /// <summary>The attempt counts in LiveSplit's history the way it does in Lockpick's (no trainer, not from a save).</summary>
    private static bool Counts(RunStatus status) => !status.CheatsUsed && !status.FromSave;

    private string PathFor(SplitSet set) => Path.Combine(_directory, Safe(set.Id) + ".lss");

    private static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
