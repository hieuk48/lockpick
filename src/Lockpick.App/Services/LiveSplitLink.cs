using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>How Lockpick works with the runner's LiveSplit (through LiveSplit's TCP server, docs/RUNS.md).</summary>
public static class LiveSplitModes
{
    public const string Off = "off";

    /// <summary>LiveSplit splits (its own autosplitter or keys); Lockpick follows its splits, undos and resets.</summary>
    public const string Follow = "follow";

    /// <summary>Lockpick splits and drives LiveSplit: start, split, undo, skip, reset, and the game time.</summary>
    public const string Drive = "drive";

    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        (Off, "Off"),
        (Follow, "Follow LiveSplit (its autosplitter splits; Lockpick follows)"),
        (Drive, "Drive LiveSplit (Lockpick splits and sets its game time)"),
    ];
}

/// <summary>
/// Talks to LiveSplit's TCP server (LiveSplit: Control → Start TCP Server, port 16834). Leaderboard runs must use
/// LiveSplit's own autosplitter, so by default Lockpick follows: LiveSplit's split index and timer phase move
/// Lockpick's run (records, overlay) and Lockpick doesn't split by itself, so nothing is split twice. Without an
/// autosplitter Lockpick can drive LiveSplit instead: it starts, splits, undoes, skips and resets it and keeps its
/// game time on the game's clock. Local only; reconnects by itself.
/// </summary>
public sealed class LiveSplitLink : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);

    private readonly RunService _runs;
    private readonly Lockpick.Core.Monitoring.IGameStateSource _source;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Task _loop;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _lastIndex = -1;
    private int _lastCurrent;
    private bool _wasRunning;

    public LiveSplitLink(RunService runs, Lockpick.Core.Monitoring.IGameStateSource source, string mode, int port, ILogger log)
    {
        _runs = runs;
        _source = source;
        _log = log;
        Mode = mode;
        Port = port;
        _runs.EventRaised += OnRunEvent;
        _runs.StatusChanged += OnRunStatus;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    public string Mode { get; private set; }

    public int Port { get; private set; }

    /// <summary>"Connected to LiveSplit", "Waiting for LiveSplit's server on port 16834…", or "Off".</summary>
    public string Status { get; private set; } = "Off";

    public event EventHandler? StatusChanged;

    public void Configure(string mode, int port)
    {
        Mode = mode;
        Port = port;
        Disconnect(); // the loop reconnects with the new settings
    }

    public void Dispose()
    {
        _runs.EventRaised -= OnRunEvent;
        _runs.StatusChanged -= OnRunStatus;
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        Disconnect();
        _stop.Dispose();
        _io.Dispose();
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Lockpick leaves the splitting to LiveSplit only while it follows a connected LiveSplit: with LiveSplit
                // closed (or the mode off) it splits by itself again, so a run is never left unsplit.
                _runs.AutoSplit = Mode != LiveSplitModes.Follow || _client is null;
                if (Mode == LiveSplitModes.Off)
                {
                    SetStatus("Off");
                    await Task.Delay(Retry, stop).ConfigureAwait(false);
                    continue;
                }

                if (_client is null)
                {
                    await ConnectAsync(stop).ConfigureAwait(false);
                    _lastIndex = -1;
                    _runs.AutoSplit = Mode != LiveSplitModes.Follow;
                    SetStatus(Mode == LiveSplitModes.Follow ? "Connected: following LiveSplit's splits." : "Connected: Lockpick's splits drive LiveSplit.");
                }

                if (Mode == LiveSplitModes.Follow)
                {
                    await FollowAsync().ConfigureAwait(false);
                }
                else if (_runs.Status.State == RunState.Running && _runs.Status.GameTime(_source.Latest) is { } time)
                {
                    // LiveSplit's game time follows the game's clock (it pauses its own; see RunStarted).
                    await SendAsync($"setgametime {Clock(time)}").ConfigureAwait(false);
                }

                await Task.Delay(Poll, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                Disconnect();
                _runs.AutoSplit = true; // LiveSplit is gone: Lockpick splits by itself until it is back
                SetStatus($"Waiting for LiveSplit's server on port {Port}; meanwhile Lockpick splits by itself.");
                try
                {
                    await Task.Delay(Retry, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConnectAsync(CancellationToken stop)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync("127.0.0.1", Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException("LiveSplit didn't answer.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        _client = client;
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        _log.LogInformation("Connected to LiveSplit on port {Port} ({Mode})", Port, Mode);
    }

    /// <summary>LiveSplit's split index and timer phase move Lockpick's run.</summary>
    private async Task FollowAsync()
    {
        var phase = await AskAsync("getcurrenttimerphase").ConfigureAwait(false);
        var index = int.TryParse(await AskAsync("getsplitindex").ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
        var running = phase is "Running" or "Paused";
        if (_runs.Status.State == RunState.Running)
        {
            if (_wasRunning && !running && phase == "NotRunning")
            {
                _runs.Reset(); // reset in LiveSplit
            }
            else if (_lastIndex >= 0 && index > _lastIndex)
            {
                for (var n = _lastIndex; n < index; n++)
                {
                    _runs.Split();
                }
            }
            else if (_lastIndex >= 0 && index >= 0 && index < _lastIndex)
            {
                for (var n = index; n < _lastIndex; n++)
                {
                    _runs.Undo();
                }
            }
            else if (phase == "Ended" && _lastIndex >= 0 && _runs.Status.State == RunState.Running)
            {
                _runs.Split(); // the last split ends the timer: the index doesn't move past it
            }
        }

        _wasRunning = running || phase == "Ended";
        _lastIndex = index;
    }

    private void OnRunEvent(object? sender, GameEvent e)
    {
        if (Mode != LiveSplitModes.Drive || _client is null)
        {
            return;
        }

        if (e is RunStarted)
        {
            Interlocked.Exchange(ref _lastCurrent, 0); // a new attempt starts LiveSplit at its first split
        }

        _ = e switch
        {
            // As LiveSplit's own server reads them (src/LiveSplit.Core/Server/CommandServer.cs): its game time stays
            // paused, so only Lockpick's setgametime moves it.
            RunStarted => SendAsync("reset", "starttimer", "alwayspausegametime", $"setgametime {Clock(TimeSpan.Zero)}"),
            SplitReached split => SendAsync($"setgametime {Clock(split.GameTime)}", "split"),
            RunEnded { Result: not RunResult.Finished } => SendAsync("reset"),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Undo and skip move Lockpick's split index without a split event: mirror them (drive mode).</summary>
    private void OnRunStatus(object? sender, RunStatus status)
    {
        var current = status.Current;
        var previous = Interlocked.Exchange(ref _lastCurrent, current);
        if (Mode != LiveSplitModes.Drive || _client is null || status.State != RunState.Running)
        {
            return;
        }

        if (current < previous)
        {
            _ = SendAsync([.. Enumerable.Repeat("unsplit", previous - current)]);
        }
        else if (current > previous)
        {
            // Skips (several when an attempt from a save starts further on); a split's own step was sent with it.
            var skips = Enumerable.Range(previous, current - previous).Count(i => status.Times.ElementAtOrDefault(i) is null);
            if (skips > 0)
            {
                _ = SendAsync([.. Enumerable.Repeat("skipsplit", skips)]);
            }
        }
    }

    private async Task SendAsync(params string[] commands)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var command in commands)
            {
                if (_writer is null)
                {
                    return;
                }

                await _writer.WriteLineAsync(command).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Disconnect();
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// Asks LiveSplit something. Only for commands that always answer (the timer phase, the split index): LiveSplit
    /// sends nothing at all for an empty answer (e.g. getgamename with no splits loaded, seen 2026-10-16), which would
    /// look like a dead connection here.
    /// </summary>
    private async Task<string?> AskAsync(string command)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is null || _reader is null)
            {
                throw new IOException("not connected");
            }

            await _writer.WriteLineAsync(command).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return (await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false))?.Trim()
                ?? throw new IOException("LiveSplit closed the connection.");
        }
        catch (OperationCanceledException)
        {
            throw new IOException("LiveSplit didn't answer.");
        }
        finally
        {
            _io.Release();
        }
    }

    private void Disconnect()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _client?.Dispose();
        (_writer, _reader, _client) = (null, null, null);
    }

    private void SetStatus(string status)
    {
        if (status != Status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>LiveSplit's time format for setgametime: h:mm:ss.fff.</summary>
    private static string Clock(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}");
}
