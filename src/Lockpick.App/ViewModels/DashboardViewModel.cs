using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>One labelled game-state value with its quality marker.</summary>
public sealed partial class FieldRow(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial string Value { get; set; } = "—";

    [ObservableProperty]
    public partial FieldStatus Status { get; set; } = FieldStatus.Unsupported;

    public string StatusText => Status switch
    {
        FieldStatus.Valid => "LIVE",
        FieldStatus.Stale => "STALE",
        FieldStatus.Unavailable => "N/A",
        _ => "UNSUPPORTED",
    };

    public bool IsValid => Status == FieldStatus.Valid;

    public bool IsStale => Status == FieldStatus.Stale;

    public bool IsInactive => Status is FieldStatus.Unavailable or FieldStatus.Unsupported;

    public void Set<T>(Field<T> field, Func<T, string> format)
    {
        Status = field.Status;
        Value = field.HasValue ? format(field.Value) : "—";
    }

    partial void OnStatusChanged(FieldStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(IsInactive));
    }
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Shown while nothing is attached: values are not available rather than unsupported.</summary>
    private static readonly GameSnapshot Detached = new()
    {
        InGameplay = Field<bool>.Unavailable,
        Character = Field<PlayableCharacter>.Unavailable,
        Health = Field<Core.State.Health>.Unavailable,
        Location = Field<RoomLocation>.Unavailable,
        Camera = Field<int>.Unavailable,
        Position = Field<Core.State.Position>.Unavailable,
        GameTime = Field<TimeSpan>.Unavailable,
    };

    private readonly Func<string, string?> _roomName;

    public DashboardViewModel(LiveState state, Func<string, string?>? roomName = null)
        : base("Dashboard", "Icon.LayoutDashboard")
    {
        State = state;
        _roomName = roomName ?? (_ => null);
        state.PropertyChanged += OnStateChanged;
        Refresh();
    }

    public LiveState State { get; }

    public FieldRow Gameplay { get; } = new("Session");

    public FieldRow Character { get; } = new("Character");

    public FieldRow Health { get; } = new("Health");

    public FieldRow Room { get; } = new("Stage / Room");

    public FieldRow Camera { get; } = new("Camera");

    public FieldRow Position { get; } = new("Position");

    public FieldRow GameTime { get; } = new("In-game time");

    public IReadOnlyList<FieldRow> Rows => [Gameplay, Character, Health, Room, Camera, Position, GameTime];

    [ObservableProperty]
    public partial string ConnectionTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessText { get; private set; } = "—";

    [ObservableProperty]
    public partial string ProfileText { get; private set; } = "—";

    [ObservableProperty]
    public partial string FileText { get; private set; } = "—";

    [ObservableProperty]
    public partial string HashText { get; private set; } = "—";

    [ObservableProperty]
    public partial string PeText { get; private set; } = "—";

    /// <summary>0–1 health fraction, or 0 when unknown.</summary>
    [ObservableProperty]
    public partial double HealthRatio { get; private set; }

    [ObservableProperty]
    public partial bool HealthLow { get; private set; }

    [ObservableProperty]
    public partial bool HealthCritical { get; private set; }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var status = State.Status;
        ConnectionTitle = status.State switch
        {
            ConnectionState.Attached => "CONNECTED",
            ConnectionState.Searching => "WAITING FOR GAME",
            ConnectionState.Unsupported => "UNSUPPORTED",
            ConnectionState.Replaying => "REPLAY",
            _ => "STOPPED",
        };
        ConnectionMessage = status.Message;
        ProcessText = status.Process is { } p ? $"PID {p.ProcessId} — {p.ExecutablePath ?? p.ProcessName}" : "—";
        ProfileText = status.Profile is { } profile ? $"{profile.DisplayName} [{profile.Id}]{Services.BuildCheck.Preview(profile)}" : status.State == ConnectionState.Unsupported ? "No matching profile" : "—";
        FileText = status.Fingerprint is { } fp ? $"{fp.FileName} — {fp.FileSize.ToString("N0", Inv)} bytes" : "—";
        HashText = status.Fingerprint?.Sha256 ?? "—";
        PeText = status.Fingerprint?.Pe is { } pe
            ? $"{pe.Machine}, linked {DateTimeOffset.FromUnixTimeSeconds(pe.TimeDateStamp):yyyy-MM-dd}, image 0x{pe.SizeOfImage:X}"
            : "—";

        var s = State.Snapshot ?? Detached;
        Gameplay.Set(s.InGameplay, v => v ? "In game" : "Title / not started");
        Character.Set(s.Character, v => v.ToString());
        Health.Set(s.Health, v => v.ToString());
        Room.Set(s.Location, v => _roomName(v.Id) is { } name
            ? $"{v.Id} {name} (stage {v.Stage})"
            : $"Stage {v.Stage} · Room {v.Room:X2} ({v.Id})");
        Camera.Set(s.Camera, v => v.ToString(Inv));
        Position.Set(s.Position, v => v.ToString());
        GameTime.Set(s.GameTime, v => $"{(int)v.TotalHours:D2}:{v.Minutes:D2}:{v.Seconds:D2}.{v.Milliseconds / 10:D2}");

        var ratio = s.Health.HasValue && s.Health.Value.Maximum is int max && max > 0
            ? Math.Clamp(s.Health.Value.Current / (double)max, 0, 1)
            : 0;
        HealthRatio = ratio;
        HealthCritical = s.Health.HasValue && ratio <= 0.25;
        HealthLow = s.Health.HasValue && ratio is > 0.25 and <= 0.6;
    }
}
