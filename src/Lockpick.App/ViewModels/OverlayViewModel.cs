using System.ComponentModel;
using System.Globalization;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;
using Lockpick.Core.Challenges;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// What the in-game overlay shows: the in-game time, HP, the room and the challenge's state (ROADMAP 0.3.0 sprint 2,
/// #54/#55). Widgets come and go with their data; nothing here can change the game.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly AppServices _services;

    public OverlayViewModel(AppServices services, LiveState state)
    {
        _services = services;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LiveState.Snapshot))
            {
                Show(state.Snapshot);
            }
        };
        services.Challenges.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => Show(s));
        Show(state.Snapshot);
        Show(services.Challenges.Status);
        SetCorner(services.Settings.Current.OverlayCorner);
    }

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Health { get; private set; } = string.Empty;

    /// <summary>HP as a fraction of max HP, for the bar.</summary>
    [ObservableProperty]
    public partial double HealthFraction { get; private set; }

    [ObservableProperty]
    public partial bool HealthLow { get; private set; }

    [ObservableProperty]
    public partial bool HealthMid { get; private set; }

    [ObservableProperty]
    public partial string Room { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasChallenge { get; private set; }

    [ObservableProperty]
    public partial bool ChallengeFailed { get; private set; }

    [ObservableProperty]
    public partial string Challenge { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial HorizontalAlignment Horizontal { get; private set; } = HorizontalAlignment.Right;

    [ObservableProperty]
    public partial VerticalAlignment Vertical { get; private set; } = VerticalAlignment.Top;

    /// <summary>Whether the overlay is showing, or why not (Settings).</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Off";

    /// <summary>Size of the widgets: they are designed for a 480-pixel-high picture and grow with the picture.</summary>
    [ObservableProperty]
    public partial double Scale { get; private set; } = 1;

    public void SetCorner(string corner)
    {
        Horizontal = corner is OverlayCorners.TopLeft or OverlayCorners.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Vertical = corner is OverlayCorners.BottomLeft or OverlayCorners.BottomRight ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    /// <param name="pictureHeightDips">The game picture's height in the overlay's own units (pixels ÷ DPI scale).</param>
    public void SetPictureHeight(double pictureHeightDips) => Scale = Math.Clamp(pictureHeightDips / 480.0, 0.75, 3.0);

    private void Show(GameSnapshot? snapshot)
    {
        var inGame = snapshot?.InGameplay is { IsValid: true, Value: true };
        Time = inGame && snapshot!.GameTime is { IsValid: true } t ? Format(t.Value) : string.Empty;
        if (inGame && snapshot!.Health is { IsValid: true } h && h.Value.Maximum is > 0 and var max)
        {
            var fraction = Math.Clamp((double)h.Value.Current / max, 0, 1);
            Health = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, h.Value.Current)} / {max}");
            HealthFraction = fraction;
            HealthLow = fraction < 0.25;
            HealthMid = fraction is >= 0.25 and < 0.75;
        }
        else
        {
            Health = string.Empty;
        }

        Room = inGame && snapshot!.Location is { IsValid: true } room
            ? _services.Adapter.RoomName(room.Value.Id) ?? room.Value.Id
            : string.Empty;
    }

    private void Show(ChallengeStatus status)
    {
        HasChallenge = status.State is ChallengeState.Running or ChallengeState.Failed;
        ChallengeFailed = status.State == ChallengeState.Failed;
        Challenge = status.State switch
        {
            ChallengeState.Running => $"{status.Challenge?.Name} · attempt {status.Attempt} · clean",
            ChallengeState.Failed => $"{status.Challenge?.Name} · failed: {status.FailReason}",
            _ => string.Empty,
        };
    }

    private static string Format(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}");
}
