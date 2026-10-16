using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Runs;

namespace Lockpick.App.ViewModels;

/// <summary>A split set in the Runs page's list (null: runs off).</summary>
public sealed record SplitSetChoice(SplitSet? Set, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A LiveSplit mode in the Runs page's list.</summary>
public sealed record LiveSplitChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One past attempt in the Runs page's history.</summary>
public sealed record RunHistoryRow(string Attempt, string Result, string Time, string When);

/// <summary>
/// Runs page: pick the splits (the community's, a key-item route, or a LiveSplit file), and the run is timed on the
/// game's own clock from the next new game, splitting by itself; the rows show each split against the player's best.
/// Attempts are kept, and the splits with the player's times can be saved as a LiveSplit file (e.g. for a submission).
/// </summary>
public sealed partial class RunsViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(200);

    private readonly RunService _runs;
    private readonly RunLibrary _library;
    private readonly SettingsStore _settings;
    private readonly LiveSplitLink _liveSplit;
    private readonly LiveState _state;
    private bool _syncing;
    private bool _visible;
    private DateTimeOffset _lastTick;

    public RunsViewModel(AppServices services, LiveState state)
        : base("Runs", "Icon.Timer")
    {
        _runs = services.Runs;
        _library = services.RunLibrary;
        _state = state;
        _runs.EventRaised += (_, e) => state.Add(e);
        _runs.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        _library.Changed += (_, _) => Dispatcher.UIThread.Post(Reload);
        state.PropertyChanged += OnStateChanged;
        StartOnLoad = services.Settings.Current.RunStartOnLoad;
        _settings = services.Settings;
        _liveSplit = services.LiveSplit;
        LiveSplitMode = LiveSplitChoices.FirstOrDefault(c => c.Id == services.Settings.Current.LiveSplitMode) ?? LiveSplitChoices[0];
        LiveSplitStatus = _liveSplit.Status;
        _liveSplit.StatusChanged += (_, _) => Dispatcher.UIThread.Post(() => LiveSplitStatus = _liveSplit.Status);
        Reload();
    }

    public System.Collections.ObjectModel.ObservableCollection<SplitSetChoice> Sets { get; } = [];

    [ObservableProperty]
    public partial SplitSetChoice? Selected { get; set; }

    [ObservableProperty]
    public partial bool StartOnLoad { get; set; }

    public IReadOnlyList<LiveSplitChoice> LiveSplitChoices { get; } = [.. LiveSplitModes.All.Select(m => new LiveSplitChoice(m.Id, m.Name))];

    /// <summary>Off, follow or drive the runner's LiveSplit.</summary>
    [ObservableProperty]
    public partial LiveSplitChoice LiveSplitMode { get; set; }

    [ObservableProperty]
    public partial string LiveSplitStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<SplitRowData> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RunHistoryRow> History { get; private set; } = [];

    [ObservableProperty]
    public partial string Headline { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RunTime { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Comparison { get; private set; } = string.Empty;

    /// <summary>Where the chosen splits come from and whether their triggers were checked in a real run.</summary>
    [ObservableProperty]
    public partial string SetNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSet { get; private set; }

    [ObservableProperty]
    public partial bool IsImported { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    public override void OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Sync();
        }
    }

    partial void OnSelectedChanged(SplitSetChoice? value)
    {
        if (!_syncing)
        {
            _library.Choose(value?.Set);
        }
    }

    partial void OnLiveSplitModeChanged(LiveSplitChoice value)
    {
        if (_liveSplit is null || value.Id == _settings.Current.LiveSplitMode)
        {
            return;
        }

        _settings.Save(_settings.Current with { LiveSplitMode = value.Id });
        _liveSplit.Configure(value.Id, _settings.Current.LiveSplitPort);
    }

    partial void OnStartOnLoadChanged(bool value)
    {
        if (!_syncing)
        {
            _library.SetStartOnLoad(value);
        }
    }

    /// <summary>Imports a LiveSplit file (the view picks it) and runs against it.</summary>
    public void Import(string path)
    {
        try
        {
            var set = _library.Import(path);
            var manual = set.Splits.Count(s => s.Trigger is ManualSplit);
            Message = manual == 0
                ? $"Imported {set.Splits.Count} splits; Lockpick splits every one by itself."
                : $"Imported {set.Splits.Count} splits; {manual} have no trigger Lockpick knows, so they need the split key (Settings → Hotkeys).";
            _library.Choose(set);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Couldn't import that file: {ex.Message}";
        }
    }

    /// <summary>Saves the chosen splits with the player's times as a LiveSplit file (the view picks where).</summary>
    public void Export(string path)
    {
        if (Selected?.Set is not { } set)
        {
            return;
        }

        try
        {
            LiveSplitFile.Write(path, "Resident Evil", set, _library.Records(set));
            Message = $"Saved {Path.GetFileName(path)}: your splits, best times and {_library.Records(set).Count} attempts, in-game time included.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>A file name for an export of the chosen set.</summary>
    public string ExportName => Selected?.Set is { } set ? $"Resident Evil - {set.Category}.lss" : "splits.lss";

    [RelayCommand]
    private void RemoveImported()
    {
        if (Selected?.Set is { ImportedFrom: not null } set)
        {
            _library.Remove(set);
            Message = $"Removed {set.Name} (its attempts stay in your runs folder).";
        }
    }

    [RelayCommand]
    private void Reset() => _runs.Reset();

    [RelayCommand]
    private void Split() => _runs.Split();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveState.Snapshot) || !_visible || _runs.Status.State != RunState.Running)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastTick < Throttle)
        {
            return;
        }

        _lastTick = now;
        Sync();
    }

    private void Reload()
    {
        _syncing = true;
        Sets.Clear();
        Sets.Add(new SplitSetChoice(null, "Off — no run timing"));
        foreach (var set in _library.Sets)
        {
            Sets.Add(new SplitSetChoice(set, set.ImportedFrom is null ? set.Name : $"{set.Name} (imported)"));
        }

        _syncing = false;
        Sync();
    }

    private void Sync()
    {
        var status = _runs.Status;
        _syncing = true;
        Selected = Sets.FirstOrDefault(c => c.Set?.Id == status.Set?.Id) ?? Sets.FirstOrDefault();
        _syncing = false;

        var set = status.Set;
        HasSet = set is not null;
        IsImported = set?.ImportedFrom is not null;
        IsRunning = status.State == RunState.Running;
        if (set is null)
        {
            (Headline, Detail, RunTime, Comparison, SetNote) = ("Runs are off",
                "Choose your splits above. Lockpick then times each run on the game's own clock (the in-game time speedrun.com uses) and splits by itself.",
                string.Empty, string.Empty, string.Empty);
            Rows = [];
            History = [];
            return;
        }

        var comparison = _library.Comparison(set);
        var now = status.GameTime(_state.Snapshot);
        Rows = RunRows.Build(status, comparison, now);
        RunTime = now is { } t ? RunRows.Clock(t) : status.State == RunState.Ready ? "0:00.0" : string.Empty;
        var pb = comparison.PersonalBest.Count > 0 ? comparison.PersonalBest[^1] : null;
        Comparison = string.Join(" · ", new[]
        {
            pb is { } p ? $"Personal best {RunRows.Clock(p)}" : "No personal best yet",
            comparison.SumOfBest is { } sob ? $"best possible {RunRows.Clock(sob)}" : null,
        }.Where(s => s is not null));
        SetNote = (set.ImportedFrom is not null ? $"From {Path.GetFileName(set.ImportedFrom)}. " : "Built in. ")
            + (set.Verified ? string.Empty : "Its split points come from the community's autosplitter and aren't yet checked in a full run with Lockpick; the split key fixes a missed one.");
        (Headline, Detail) = status.State switch
        {
            RunState.Ready => ($"{set.Name}: ready",
                StartOnLoad ? "Start a new game or load a save: the run starts by itself." : "Start a new game in Resident Evil: the run starts by itself (as on the leaderboard, at New Game)."),
            RunState.Running => ($"Attempt {status.Attempt}" + (status.FromSave ? " (from a save)" : string.Empty),
                $"Split {Math.Min(status.Current + 1, set.Splits.Count)} of {set.Splits.Count}: {set.Splits[Math.Min(status.Current, set.Splits.Count - 1)].Name}"
                + (status.CheatsUsed ? ". The trainer was on: this attempt won't count as a personal best." : ".")),
            RunState.Finished => ($"Attempt {status.Attempt} finished in {RunRows.Clock(status.EndGameTime ?? TimeSpan.Zero)}",
                (status.CheatsUsed ? "The trainer was on, so it doesn't count as a personal best. " : string.Empty) + "Start a new game for the next attempt."),
            _ => ($"Attempt {status.Attempt}: {Result(status.Result)}", "Start a new game for the next attempt."),
        };
        History = [.. _library.Records(set).AsEnumerable().Reverse().Take(10).Select(r => new RunHistoryRow(
            $"#{r.Attempt}", Result(r.Result) + (r.CheatsUsed ? " · trainer on" : string.Empty) + (r.FromSave ? " · from a save" : string.Empty),
            RunRows.Clock(r.GameTime), r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)))];
    }

    private static string Result(RunResult? result) => result switch
    {
        RunResult.Finished => "finished",
        RunResult.Died => "died",
        RunResult.QuitToTitle => "back to the title",
        RunResult.GameClosed => "the game closed",
        RunResult.Reset => "reset",
        _ => string.Empty,
    };
}
