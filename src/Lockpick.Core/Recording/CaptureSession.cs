using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;

namespace Lockpick.Core.Recording;

/// <summary>Build identity recorded in a capture (no paths: the folder may contain a user name).</summary>
public sealed record CapturedBuild(string FileName, long FileSize, string Sha256, string? PeTimestamp, string? SizeOfImage, string? ProfileId, string Result, DateTimeOffset SeenAt);

/// <summary>
/// A moment the player marked while capturing (markers.jsonl), so the data around it can be matched to what happened
/// on screen. <see cref="At"/> is the wall clock the memory log and traces also use.
/// </summary>
/// <param name="Kind">"marker" (added by the player), "answer" or "skipped" (an ask-mode question), or "auto" (a story
/// change ask mode put down to a player action instead of asking).</param>
/// <param name="Changes">For questions: the unexplained changes that were asked about.</param>
public sealed record CaptureMarker(int Number, DateTimeOffset At, int Attachment, double? GameSeconds, string? Room, string Note, string Kind = "marker", IReadOnlyList<string>? Changes = null);

/// <summary>Contents of capture-info.json.</summary>
public sealed record CaptureInfo(string LockpickVersion, string Os, DateTimeOffset StartedAt)
{
    public List<CapturedBuild> Builds { get; init; } = [];

    public int Attachments { get; set; }

    public long Samples { get; set; }

    public long Events { get; set; }

    /// <summary>Records written to the memory logs (memory-N.bin) of the latest attachment.</summary>
    public long MemoryRecords { get; set; }

    public int Markers { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// Records a play session for research: every raw profile-field change (trace-N.txt, the same format as
/// <c>lockpick-probe trace</c>), the normalized recording (recording-N.jsonl), events (events-N.txt) and the build
/// fingerprints seen (capture-info.json), one set of files per game attachment, plus the player's markers
/// (markers.jsonl). <see cref="Stop"/> zips the session.
/// Records no machine names, user names or paths.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private static readonly JsonSerializerOptions InfoJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions MarkerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Longest marker note kept; notes are single lines.</summary>
    public const int MaxMarkerNoteLength = 200;
    private static readonly IReadOnlyDictionary<string, Field<long>> NoRawFields = new Dictionary<string, Field<long>>();
    private static readonly TimeSpan MinTimerOnlyInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Memory regions are logged at 10 Hz: persistent state (flags, items, enemy HP, rooms) is still captured, and
    /// per-frame churn (animation, positions) costs a third. Fields are still traced at the full poll rate.
    /// </summary>
    private static readonly TimeSpan RegionInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Raw fields that change continuously; printed as context on every trace line instead of as changes.</summary>
    public static readonly IReadOnlySet<string> ContextFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gameTimer", "positionX", "positionY", "positionZ", "yaw",
    };

    private readonly IGameStateSource _source;
    private readonly string _gameId;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CaptureInfo _info;
    private readonly List<CaptureMarker> _markers = [];

    private StreamWriter? _trace;
    private StreamWriter? _events;
    private SessionRecorder? _recorder;
    private RegionLogWriter? _regionLog;
    private DateTimeOffset _lastRegionAt;
    private IReadOnlyDictionary<string, Field<long>>? _lastRaw;
    private GameSnapshot? _lastRecorded;
    private DateTimeOffset _lastFlush;
    private bool _stopped;

    public CaptureSession(IGameStateSource source, string rootDirectory, string gameId, string appVersion, TimeProvider? time = null)
    {
        _source = source;
        _gameId = gameId;
        _time = time ?? TimeProvider.System;
        var now = _time.GetLocalNow();
        Name = $"capture-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        RootDirectory = rootDirectory;
        Directory = Path.Combine(rootDirectory, Name);
        System.IO.Directory.CreateDirectory(Directory);
        _info = new CaptureInfo(appVersion, RuntimeInformation.OSDescription, now);
        WriteInfo();

        // Ask the source for raw memory regions while capturing (see RegionLogWriter).
        _source.RecordRegions = true;
        _source.StatusChanged += OnStatus;
        _source.Polled += OnPolled;
        _source.SnapshotChanged += OnSnapshot;
        _source.EventRaised += OnEvent;

        // Started while already attached: begin recording immediately.
        OnStatus(this, _source.Status);
    }

    public string Name { get; }

    public string RootDirectory { get; }

    public string Directory { get; }

    public bool IsRecordingGame
    {
        get
        {
            lock (_gate)
            {
                return _trace is not null;
            }
        }
    }

    public long Samples
    {
        get
        {
            lock (_gate)
            {
                return _info.Samples;
            }
        }
    }

    public long EventCount
    {
        get
        {
            lock (_gate)
            {
                return _info.Events;
            }
        }
    }

    public int Attachments
    {
        get
        {
            lock (_gate)
            {
                return _info.Attachments;
            }
        }
    }

    public IReadOnlyList<CaptureMarker> Markers
    {
        get
        {
            lock (_gate)
            {
                return [.. _markers];
            }
        }
    }

    /// <summary>
    /// Marks the current moment, with the game time and room when a game is being recorded. The note can be empty
    /// (e.g. from a hotkey while playing) and filled in later with <see cref="SetMarkerNote"/>.
    /// </summary>
    public CaptureMarker AddMarker(string? note = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var latest = _trace is not null ? _source.Latest : null;
            var marker = new CaptureMarker(
                _markers.Count + 1,
                _time.GetLocalNow(),
                _trace is not null ? _info.Attachments : 0,
                latest is not null && latest.GameTime.TryGet(out var gameTime) ? Math.Round(gameTime.TotalSeconds, 2) : null,
                latest is not null && latest.Location.TryGet(out var location) ? location.Id : null,
                CleanNote(note));
            _markers.Add(marker);
            _events?.WriteLine($"[{marker.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] Marker {marker.Number}{(marker.Note.Length > 0 ? ": " + marker.Note : string.Empty)}");
            _events?.Flush();
            WriteMarkers();
            return marker;
        }
    }

    /// <summary>
    /// Records the player's answer to an ask-mode question (or that it was skipped), placed at the moment the first
    /// unexplained change happened.
    /// </summary>
    public CaptureMarker AddAnswer(Insight.PendingQuestion question, string? answer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var note = CleanNote(answer);
            var marker = new CaptureMarker(
                _markers.Count + 1,
                question.At.ToLocalTime(),
                _trace is not null ? _info.Attachments : 0,
                question.GameTime is { } t ? Math.Round(t.TotalSeconds, 2) : null,
                question.Room,
                note,
                note.Length > 0 ? "answer" : "skipped",
                [.. question.Changes.Select(c => c.Description)]);
            _markers.Add(marker);
            _events?.WriteLine($"[{_time.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] Question {marker.Number} ({string.Join("; ", marker.Changes!)}): {(note.Length > 0 ? note : "skipped")}");
            _events?.Flush();
            WriteMarkers();
            return marker;
        }
    }

    /// <summary>Records a story change ask mode attributed to a player action (kind "auto"), so it can be labelled later.</summary>
    public CaptureMarker AddAttribution(Insight.AttributedChange attributed)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var marker = new CaptureMarker(
                _markers.Count + 1,
                attributed.At.ToLocalTime(),
                _trace is not null ? _info.Attachments : 0,
                attributed.GameTime is { } t ? Math.Round(t.TotalSeconds, 2) : null,
                attributed.Room,
                CleanNote(attributed.Action),
                "auto",
                [attributed.Change.Description]);
            _markers.Add(marker);
            WriteMarkers();
            return marker;
        }
    }

    /// <summary>Sets the note of an earlier marker. Returns false if there is no such marker or the capture has stopped.</summary>
    public bool SetMarkerNote(int number, string? note)
    {
        lock (_gate)
        {
            var index = _markers.FindIndex(m => m.Number == number);
            if (_stopped || index < 0)
            {
                return false;
            }

            _markers[index] = _markers[index] with { Note = CleanNote(note) };
            WriteMarkers();
            return true;
        }
    }

    /// <summary>Same line format as <c>lockpick-probe trace</c>; tools/fixtures/trace_to_fixture.py reads it.</summary>
    public static string TraceLine(DateTime at, IReadOnlyDictionary<string, Field<long>> raw, IEnumerable<string> changed) =>
        $"[{at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] "
        + string.Join(", ", changed.Select(k => $"{k}={Hex(raw[k])}"))
        + $" | timer={Hex(raw.GetValueOrDefault("gameTimer"))} pos={Hex(raw.GetValueOrDefault("positionX"))},{Hex(raw.GetValueOrDefault("positionZ"))}";

    /// <summary>Stops recording and writes <c>LockpickCapture-*.zip</c> next to the session folder. Returns the zip path.</summary>
    public string Stop()
    {
        Dispose();
        var zip = Path.Combine(RootDirectory, $"LockpickCapture-{Name["capture-".Length..]}.zip");
        if (!File.Exists(zip))
        {
            ZipFile.CreateFromDirectory(Directory, zip, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        }

        return zip;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            Unsubscribe();
            CloseAttachment();
            _info.EndedAt = _time.GetLocalNow();
            WriteInfo();
        }
    }

    private static string CleanNote(string? note)
    {
        var line = string.Join(' ', (note ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxMarkerNoteLength ? line[..MaxMarkerNoteLength] : line;
    }

    private void WriteMarkers()
    {
        _info.Markers = _markers.Count;
        File.WriteAllLines(Path.Combine(Directory, "markers.jsonl"), _markers.Select(m => JsonSerializer.Serialize(m, MarkerJson)));
        WriteInfo();
    }

    private static string Hex(Field<long> f) => f.HasValue ? $"0x{f.Value:X}" : f.Status.ToString();

    private void Unsubscribe()
    {
        _source.RecordRegions = false;
        _source.StatusChanged -= OnStatus;
        _source.Polled -= OnPolled;
        _source.SnapshotChanged -= OnSnapshot;
        _source.EventRaised -= OnEvent;
    }

    private void OnStatus(object? sender, MonitorStatus status)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (status.Fingerprint is { } fp && !_info.Builds.Exists(b => b.Sha256 == fp.Sha256))
            {
                _info.Builds.Add(new CapturedBuild(
                    fp.FileName,
                    fp.FileSize,
                    fp.Sha256,
                    fp.Pe is { } pe ? $"0x{pe.TimeDateStamp:X8}" : null,
                    fp.Pe is { } pe2 ? $"0x{pe2.SizeOfImage:X}" : null,
                    status.Profile?.Id,
                    status.Message,
                    _time.GetLocalNow()));
                WriteInfo();
            }

            var recordable = status.State is ConnectionState.Attached or ConnectionState.Replaying;
            if (recordable && _trace is null)
            {
                OpenAttachment(status.Profile);
            }
            else if (!recordable && _trace is not null)
            {
                CloseAttachment();
                WriteInfo();
            }
        }
    }

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_trace is null)
            {
                return;
            }

            var raw = snapshot.RawFields;
            var changed = _lastRaw is null
                ? raw.Keys.ToList()
                : raw.Keys.Where(k => !ContextFields.Contains(k) && (!_lastRaw.TryGetValue(k, out var old) || old != raw[k])).ToList();
            if (changed.Count > 0)
            {
                _trace.WriteLine(TraceLine(snapshot.CapturedAt.LocalDateTime, raw, changed));
            }

            _lastRaw = raw;
            _info.Samples++;
            if (_regionLog is not null && snapshot.Regions.Count > 0 && snapshot.CapturedAt - _lastRegionAt >= RegionInterval)
            {
                _regionLog.Append(snapshot.CapturedAt, snapshot.Regions);
                _lastRegionAt = snapshot.CapturedAt;
                _info.MemoryRecords = _regionLog.Records;
            }

            // Keep files usable if the app is killed; at most one flush per second.
            var now = _time.GetUtcNow();
            if (now - _lastFlush >= TimeSpan.FromSeconds(1))
            {
                _trace.Flush();
                _events?.Flush();
                _recorder?.Flush();
                _regionLog?.Flush();
                _lastFlush = now;
            }
        }
    }

    private void OnSnapshot(object? sender, GameSnapshot snapshot)
    {
        lock (_gate)
        {
            Record(snapshot);
        }
    }

    /// <summary>
    /// Appends a compact frame: raw fields are omitted (trace-N.txt already has every raw change), and while only
    /// the in-game timer moves, at most one frame per second is written. A live session otherwise records every
    /// 30 Hz poll (~64 MB per 20 minutes in the first live capture).
    /// </summary>
    private void Record(GameSnapshot snapshot)
    {
        if (_recorder is null)
        {
            return;
        }

        var compact = snapshot with { RawFields = NoRawFields };
        if (_lastRecorded is { } last
            && compact.HasSameStateAs(last with { GameTime = compact.GameTime })
            && compact.GameTime.TryGet(out var now)
            && last.GameTime.TryGet(out var before)
            && now - before < MinTimerOnlyInterval)
        {
            return;
        }

        _recorder.Append(compact);
        _lastRecorded = compact;
    }

    private void OnEvent(object? sender, GameEvent gameEvent)
    {
        lock (_gate)
        {
            if (_events is null)
            {
                return;
            }

            _events.WriteLine($"[{gameEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {gameEvent.Describe()}");
            _info.Events++;
        }
    }

    private void OpenAttachment(Profiles.VersionProfile? profile)
    {
        var n = ++_info.Attachments;
        _trace = new StreamWriter(Path.Combine(Directory, $"trace-{n}.txt"));
        _events = new StreamWriter(Path.Combine(Directory, $"events-{n}.txt"));
        _recorder = SessionRecorder.Create(Path.Combine(Directory, $"recording-{n}.jsonl"), _gameId, profile?.Id, _time.GetUtcNow());
        if (profile is { CaptureRegions.Count: > 0 })
        {
            _regionLog = new RegionLogWriter(File.Create(Path.Combine(Directory, $"memory-{n}.bin")), profile.CaptureRegions);
            _lastRegionAt = DateTimeOffset.MinValue;
        }

        _lastRaw = null;

        // Started mid-session: record the current state first, or a quiet session would leave the recording empty.
        _lastRecorded = null;
        if (_source.Latest is { } current)
        {
            Record(current);
        }

        WriteInfo();
    }

    private void CloseAttachment()
    {
        _trace?.Dispose();
        _events?.Dispose();
        _recorder?.Dispose();
        _regionLog?.Dispose();
        _regionLog = null;
        _trace = null;
        _events = null;
        _recorder = null;
        _lastRaw = null;
    }

    private void WriteInfo() =>
        File.WriteAllText(Path.Combine(Directory, "capture-info.json"), JsonSerializer.Serialize(_info, InfoJson));
}
