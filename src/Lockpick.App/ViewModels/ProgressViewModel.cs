using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Services;
using Lockpick.Core.Insight;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>One kind of progress in an area, e.g. "Items 33 / 51", with what's left.</summary>
public sealed record ProgressGroupRow(string Title, int Done, int Total, IReadOnlyList<string> Left)
{
    public string Count => string.Create(CultureInfo.InvariantCulture, $"{Done} / {Total}");

    public double Percent => Total == 0 ? 0 : 100.0 * Done / Total;

    public bool HasLeft => Left.Count > 0;

    public string LeftHeader => string.Create(CultureInfo.InvariantCulture, $"{Left.Count} left");
}

public sealed record ProgressAreaRow(string Name, IReadOnlyList<ProgressGroupRow> Groups);

/// <summary>
/// Progress page: per area, the items, documents and maps taken, rooms explored and enemies defeated, and the story so
/// far — read from the game's flags (read-only), so it also works from a loaded save.
/// </summary>
public sealed partial class ProgressViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

    private readonly AppServices _services;
    private IProgressTracker? _tracker;
    private DateTimeOffset _lastUpdate;
    private string? _lastSignature;
    private bool _visible;

    public ProgressViewModel(AppServices services, LiveState state)
        : base("Progress", "Icon.ListChecks")
    {
        _services = services;
        State = state;
        state.PropertyChanged += OnStateChanged;
    }

    public LiveState State { get; }

    public ObservableCollection<ProgressAreaRow> Areas { get; } = [];

    public ObservableCollection<string> Story { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Start or load a game to see your progress.";

    [ObservableProperty]
    public partial string StoryHeader { get; private set; } = "STORY SO FAR";

    public override void OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Refresh(force: true);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_visible && e.PropertyName == nameof(LiveState.Snapshot))
        {
            Refresh(force: false);
        }
    }

    private void Refresh(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastUpdate < Throttle)
        {
            return;
        }

        _lastUpdate = now;
        var profile = State.Status.Profile ?? (_services.Adapter.Profiles.Count > 0 ? _services.Adapter.Profiles[0] : null);
        _tracker ??= profile is null ? null : _services.Adapter.CreateProgressTracker(profile);
        if (_tracker is null || State.Snapshot is not { } snapshot || _tracker.Evaluate(snapshot) is not { } report)
        {
            Summary = _tracker is null ? "Progress isn't available for this game version yet." : "Start or load a game to see your progress.";
            if (Areas.Count > 0 || Story.Count > 0)
            {
                Areas.Clear();
                Story.Clear();
                _lastSignature = null;
            }

            return;
        }

        Summary = Describe(snapshot);
        var signature = string.Join('|', report.Areas.SelectMany(a => a.Groups.Select(g => $"{a.Area}{g.Title}{g.Done}")))
            + $"|{report.Story.Count}|{report.UnnamedStory}";
        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        Areas.Clear();
        foreach (var area in report.Areas)
        {
            Areas.Add(new ProgressAreaRow(area.Area, [.. area.Groups.Select(g => new ProgressGroupRow(
                g.Title, g.Done, g.Total, [.. g.Entries.Where(e => !e.Done).Select(e => e.Name == e.Room ? e.Name : $"{e.Name} — {e.Room}")]))]));
        }

        Story.Clear();
        foreach (var line in report.Story)
        {
            Story.Add(line);
        }

        StoryHeader = string.Create(CultureInfo.InvariantCulture,
            $"STORY SO FAR · {report.Story.Count} named{(report.UnnamedStory > 0 ? $", {report.UnnamedStory} not named yet" : string.Empty)}");
    }

    private static string Describe(GameSnapshot s)
    {
        var who = s.Character.TryGet(out var c) ? c.ToString() : "Player";
        var when = s.GameTime.TryGet(out var t) ? $" · game time {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : string.Empty;
        var where = s.Location.TryGet(out var room) ? $" · room {room.Id}" : string.Empty;
        return $"{who}{when}{where}";
    }
}
