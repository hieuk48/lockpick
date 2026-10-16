using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Insight;
using Lockpick.Core.Logging;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.Recording;
using Lockpick.Core.State;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.ViewModels;

public sealed record RawFieldRow(string Name, string Address, string Type, string Verification, string Value, string Status, bool IsTrusted);

public sealed record LogRow(string Time, string Level, string Category, string Message, bool IsWarning);

/// <summary>A capture marker; editing the note writes it into the running capture.</summary>
public sealed partial class MarkerRow(CaptureMarker marker, Func<int, string, bool> setNote) : ObservableObject
{
    public int Number { get; } = marker.Number;

    public string Label { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"#{marker.Number} · {marker.At:HH:mm:ss}{(marker.Room is { } room ? $" · room {room}" : string.Empty)}{(marker.GameSeconds is { } t ? $" · game {TimeSpan.FromSeconds(t):h\\:mm\\:ss}" : string.Empty)}");

    [ObservableProperty]
    public partial string Note { get; set; } = marker.Note;

    [ObservableProperty]
    public partial bool IsEditable { get; set; } = true;

    partial void OnNoteChanged(string value) => setNote(Number, value);
}

/// <summary>Debug page: session capture for research, monitor statistics, raw profile fields and the live log.</summary>
public sealed partial class DebugViewModel : PageViewModel, IDisposable, Lockpick.Api.ICaptureControl
{
    private const int MaxLogRows = 400;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly ILogger _logger;
    private CaptureSession? _capture;
    private QuestionScheduler? _questions;
    private volatile bool _asking;

    public DebugViewModel(AppServices services, LiveState state)
        : base("Debug", "Icon.Bug")
    {
        _services = services;
        _logger = services.LoggerFactory.CreateLogger("Debug");
        State = state;
        LogFile = services.LogProvider.FilePath ?? "(file logging disabled)";
        TimerInfo = services.IsReplay ? "Replay" : services.HighResolutionTimer ? "High-resolution waitable timer" : "Default OS timer (≈15.6 ms granularity)";
        foreach (var entry in services.LogProvider.RecentEntries.TakeLast(MaxLogRows).Reverse())
        {
            LogRows.Add(ToRow(entry));
        }

        services.LogProvider.EntryLogged += entry => Dispatcher.UIThread.Post(() => AddLog(entry), DispatcherPriority.Background);
        services.MarkerHotkeyPressed += (_, _) => Dispatcher.UIThread.Post(OnMarkerHotkey);
        RefreshHotkeyInfo();
        state.PropertyChanged += OnStateChanged;

        // Capture status ticks even while the page is hidden so the nav badge stays current.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        RefreshRawFields();
    }

    public LiveState State { get; }

    public string LogFile { get; }

    public string TimerInfo { get; }

    public ObservableCollection<RawFieldRow> RawFields { get; } = [];

    public ObservableCollection<LogRow> LogRows { get; } = [];

    public ObservableCollection<MarkerRow> Markers { get; } = [];

    [ObservableProperty]
    public partial string HotkeyInfo { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MarkerNote { get; set; } = string.Empty;

    /// <summary>
    /// Ask mode: while capturing, pause the game and ask the player about changes Lockpick can't explain. Off at every
    /// start so ordinary captures are never interrupted.
    /// </summary>
    [ObservableProperty]
    public partial bool AskAboutUnknownEvents { get; set; }

    /// <summary>Shows a question to the player; set by the main window.</summary>
    public Func<QuestionViewModel, Task<string?>>? AskPlayer { get; set; }

    [ObservableProperty]
    public partial string PollStats { get; private set; } = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand), nameof(StopCaptureCommand), nameof(AddMarkerCommand))]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    public partial string CaptureStatus { get; private set; } = "Not capturing.";

    [ObservableProperty]
    public partial string? LastCaptureZip { get; private set; }

    private bool IsVisible { get; set; }

    public override void OnVisibilityChanged(bool visible)
    {
        IsVisible = visible;
        if (visible)
        {
            RefreshHotkeyInfo();
            RefreshStats();
            RefreshRawFields();
        }
    }

    // Capture control for the API (any thread): everything happens on the UI thread, as from the page's buttons.
    Task<string?> Lockpick.Api.ICaptureControl.StartAsync() => Dispatcher.UIThread.InvokeAsync<string?>(() =>
    {
        if (IsCapturing)
        {
            return "a capture is already running";
        }

        StartCapture();
        return IsCapturing ? null : CaptureStatus;
    }).GetTask();

    Task<string?> Lockpick.Api.ICaptureControl.StopAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (!IsCapturing)
        {
            return null;
        }

        await StopCaptureAsync().ConfigureAwait(true);
        return LastCaptureZip ?? CaptureStatus;
    });

    Task<int?> Lockpick.Api.ICaptureControl.MarkAsync(string? note) => Dispatcher.UIThread.InvokeAsync<int?>(() =>
    {
        if (_capture is not { } capture)
        {
            return null;
        }

        var marker = capture.AddMarker(note);
        Markers.Insert(0, new MarkerRow(marker, capture.SetMarkerNote));
        _logger.LogInformation("Marker {Number} added through the API", marker.Number);
        return marker.Number;
    }).GetTask();

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private void StartCapture()
    {
        try
        {
            _capture = new CaptureSession(_services.Source, AppPaths.Captures, _services.Adapter.GameId, _services.AppVersion);
            IsCapturing = true;
            LastCaptureZip = null;
            Markers.Clear();
            _services.Source.Polled += OnPolledForQuestions;
            _logger.LogInformation("Capture started: {Directory}", _capture.Directory);
            UpdateCaptureStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = $"Couldn't start capture: {ex.Message}";
        }
    }

    private bool CanStartCapture() => !IsCapturing;

    [RelayCommand(CanExecute = nameof(IsCapturing))]
    private async Task StopCaptureAsync()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        _capture = null;
        _services.Source.Polled -= OnPolledForQuestions;
        _questions = null;
        IsCapturing = false;
        foreach (var row in Markers)
        {
            row.IsEditable = false;
        }

        CaptureStatus = "Saving capture...";
        try
        {
            var zip = await Task.Run(capture.Stop).ConfigureAwait(true);
            LastCaptureZip = zip;
            CaptureStatus = $"Saved {capture.Samples:N0} samples and {capture.EventCount:N0} events.";
            _logger.LogInformation("Capture saved: {Zip}", zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = $"Couldn't save the capture: {ex.Message}. The raw files are in {capture.Directory}.";
        }
        finally
        {
            capture.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsCapturing))]
    private void AddMarker()
    {
        if (_capture is not { } capture)
        {
            return;
        }

        var marker = capture.AddMarker(MarkerNote);
        Markers.Insert(0, new MarkerRow(marker, capture.SetMarkerNote));
        MarkerNote = string.Empty;
    }

    partial void OnAskAboutUnknownEventsChanged(bool value)
    {
        if (value && _services.LeaderboardGuard.Enabled)
        {
            // Ask mode pauses the game, which a leaderboard run can't have.
            AskAboutUnknownEvents = false;
            CaptureStatus = "Ask mode is off during leaderboard runs: it pauses the game (Runs → Leaderboard run).";
            return;
        }

        // Start fresh: changes from before the mode was switched on are not asked about.
        var profile = State.Status.Profile ?? _services.Adapter.Profiles.FirstOrDefault();
        _questions = value && profile is not null && _services.Adapter.CreateChangeExplainer(profile) is { } explainer
            ? new QuestionScheduler(explainer)
            : null;
        if (value && _questions is null)
        {
            CaptureStatus = "This game version can't tell known from unknown events yet, so ask mode is unavailable.";
        }
    }

    /// <summary>Monitor thread: feeds every poll to the scheduler and asks on the UI thread when a question is ready.</summary>
    private void OnPolledForQuestions(object? sender, GameSnapshot snapshot)
    {
        if (_asking || _questions is not { } questions || _capture is null)
        {
            return;
        }

        var question = questions.Feed(snapshot);
        foreach (var attributed in questions.TakeAttributed())
        {
            try
            {
                _capture?.AddAttribution(attributed);
            }
            catch (ObjectDisposedException)
            {
                return; // the capture was stopped meanwhile
            }
        }

        if (question is not null)
        {
            _asking = true;
            Dispatcher.UIThread.Post(() => _ = AskAsync(question));
        }
    }

    private async Task AskAsync(PendingQuestion question)
    {
        try
        {
            if (_capture is not { } capture || AskPlayer is not { } ask)
            {
                return;
            }

            var pid = State.Status.State == ConnectionState.Attached ? State.Status.Process?.ProcessId : null;
            var problem = pid is { } id ? _services.Pause.Pause(id) : "not attached to a running game";
            string? answer;
            try
            {
                answer = await ask(new QuestionViewModel(question, problem)).ConfigureAwait(true);
            }
            finally
            {
                _services.Pause.Resume();
            }

            if (_capture == capture)
            {
                var marker = capture.AddAnswer(question, answer);
                Markers.Insert(0, new MarkerRow(marker, capture.SetMarkerNote));
            }
        }
        catch (ObjectDisposedException)
        {
            // The capture was stopped while the question was open.
        }
        finally
        {
            _asking = false;
        }
    }

    private void RefreshHotkeyInfo() =>
        HotkeyInfo = _services.MarkerHotkeyName is { } key
            ? $"Press {key} while playing to mark a moment, then describe it here (e.g. \"pushed the statue\"). Change the key in Settings."
            : "No marker hotkey (choose one in Settings); markers can be added here.";

    private void OnMarkerHotkey()
    {
        if (_capture is { } capture)
        {
            var marker = capture.AddMarker();
            Markers.Insert(0, new MarkerRow(marker, capture.SetMarkerNote));
            _logger.LogInformation("Marker {Number} added by hotkey", marker.Number);
        }
    }

    [RelayCommand]
    private void OpenCapturesFolder()
    {
        Directory.CreateDirectory(AppPaths.Captures);
        var args = LastCaptureZip is { } zip && File.Exists(zip) ? $"/select,\"{zip}\"" : $"\"{AppPaths.Captures}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CaptureStatus = $"Couldn't open Explorer: {ex.Message}";
        }
    }

    /// <summary>Called on app shutdown: a running capture is saved, never lost.</summary>
    public void Dispose()
    {
        _timer.Stop();
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            try
            {
                capture.Stop();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Capture could not be zipped on exit; files remain in {Directory}", capture.Directory);
            }

            capture.Dispose();
        }
    }

    private void Tick()
    {
        if (IsCapturing)
        {
            UpdateCaptureStatus();
        }

        if (IsVisible)
        {
            RefreshStats();
        }
    }

    private void UpdateCaptureStatus()
    {
        if (_capture is not { } c)
        {
            return;
        }

        var game = c.IsRecordingGame ? "recording the game" : "waiting for a supported game";
        CaptureStatus = string.Create(CultureInfo.InvariantCulture, $"Capturing — {game}. {c.Samples:N0} samples, {c.EventCount:N0} events.");
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshRawFields();
        }
    }

    private void RefreshStats()
    {
        var s = State.Source.Statistics;
        PollStats = State.Status.State == ConnectionState.Attached
            ? string.Create(CultureInfo.InvariantCulture, $"{s.Polls:N0} polls ({s.FailedPolls:N0} failed) · {s.MeasuredPollHz:F1} Hz · read avg {s.AveragePollMs:F3} ms · max {s.MaxPollMs:F3} ms · last {s.LastPollMs:F3} ms")
            : State.Status.State == ConnectionState.Replaying ? $"{s.Polls:N0} frames replayed" : "Not attached";
    }

    private void RefreshRawFields()
    {
        RawFields.Clear();
        var profile = State.Status.Profile ?? (_services.Adapter.Profiles.Count > 0 ? _services.Adapter.Profiles[0] : null);
        if (profile is null)
        {
            return;
        }

        var raw = State.Snapshot?.RawFields;
        foreach (var f in profile.Fields)
        {
            var value = raw is not null && raw.TryGetValue(f.Name, out var v) ? v : (Field<long>?)null;
            RawFields.Add(new RawFieldRow(
                f.Name,
                $"0x{f.Address:X}",
                f.Type.ToString().ToLowerInvariant() + (f.Mask is { } m ? $" & 0x{m:X}" : string.Empty),
                f.Verification.ToString().ToLowerInvariant(),
                value is { HasValue: true } fv ? string.Create(CultureInfo.InvariantCulture, $"{fv.Value} (0x{fv.Value:X})") : "—",
                value?.Status.ToString() ?? "—",
                f.Verification >= Verification.Static));
        }
    }

    private void AddLog(LogEntry entry)
    {
        LogRows.Insert(0, ToRow(entry));
        if (LogRows.Count > MaxLogRows)
        {
            LogRows.RemoveAt(LogRows.Count - 1);
        }
    }

    private static LogRow ToRow(LogEntry e) => new(
        e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        e.Level.ToString().ToUpperInvariant(),
        e.Category,
        e.Exception is null ? e.Message : $"{e.Message} — {e.Exception.Split('\n')[0]}",
        e.Level >= LogLevel.Warning);
}
