using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Runs;

namespace Lockpick.App.ViewModels;

/// <summary>One leaderboard filter on the Runs page (e.g. Character: Jill).</summary>
public sealed partial class LeaderboardFilterRow : ObservableObject
{
    private readonly Action _changed;
    private bool _loading = true;

    public LeaderboardFilterRow(LeaderboardFilter filter, string? chosen, Action changed)
    {
        Filter = filter;
        _changed = changed;
        Selected = filter.Choices.FirstOrDefault(c => c.Key == chosen) ?? filter.Choices[0];
        _loading = false;
    }

    public LeaderboardFilter Filter { get; }

    public string Label => Filter.Label;

    public IReadOnlyList<LeaderboardChoice> Choices => Filter.Choices;

    [ObservableProperty]
    public partial LeaderboardChoice Selected { get; set; }

    partial void OnSelectedChanged(LeaderboardChoice value)
    {
        if (!_loading)
        {
            _changed();
        }
    }
}

/// <summary>A run in the Runs page's leaderboard list.</summary>
public sealed record LeaderboardRow(string Place, string Runner, string Time, string Date, bool IsYou);

/// <summary>
/// The Runs page's speedrun.com leaderboard for the chosen splits: the world record, the top runs, where the player's
/// best here would place and their own run on the board. Off until the player turns it on (it goes online); boards
/// are cached for an hour. Data from speedrun.com, CC BY-NC 4.0 — credited on the page.
/// </summary>
public sealed partial class LeaderboardViewModel : ObservableObject
{
    private const int TopShown = 5;

    private readonly AppServices _services;
    private SplitSet? _set;
    private int _loads;

    public LeaderboardViewModel(AppServices services)
    {
        _services = services;
        Enabled = services.Settings.Current.SpeedrunComLeaderboard;
        services.SpeedrunAccount.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync(refresh: false));
    }

    public LeaderboardDefinition? Board => _services.RunLibrary.Rules?.Leaderboard;

    public bool Available => Board is not null;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<LeaderboardFilterRow> Filters { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LeaderboardRow> Top { get; private set; } = [];

    [ObservableProperty]
    public partial string Record { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string YourPlace { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Weblink { get; private set; }

    public string Credit => "Leaderboard from speedrun.com (CC BY-NC 4.0). Lockpick shows it; it doesn't change it.";

    partial void OnEnabledChanged(bool value)
    {
        if (_services.Settings.Current.SpeedrunComLeaderboard != value)
        {
            _services.Settings.Save(_services.Settings.Current with { SpeedrunComLeaderboard = value });
        }

        if (value)
        {
            _ = LoadAsync(refresh: false);
        }
        else
        {
            Clear("Off: Lockpick doesn't go online for leaderboards.");
        }
    }

    /// <summary>The splits changed: pick their board and show it.</summary>
    public void Show(SplitSet? set)
    {
        if (set?.Id == _set?.Id && Filters.Count > 0)
        {
            return;
        }

        _set = set;
        Filters.Clear();
        if (Board is not { } board || set is null)
        {
            Clear(string.Empty);
            return;
        }

        var suggested = _services.RunLibrary.Rules!.LeaderboardFor(set);
        var saved = _services.Settings.Current.SpeedrunComBoards.TryGetValue(set.Id, out var text) ? Parse(text) : null;
        foreach (var filter in board.Filters)
        {
            var chosen = saved?.GetValueOrDefault(filter.Key) ?? suggested.GetValueOrDefault(filter.Key);
            Filters.Add(new LeaderboardFilterRow(filter, chosen, OnFilterChanged));
        }

        _ = LoadAsync(refresh: false);
    }

    /// <summary>Your best here changed (an attempt ended): where it would place.</summary>
    public void Refresh() => _ = LoadAsync(refresh: false);

    [RelayCommand]
    private Task Reload() => LoadAsync(refresh: true);

    private void OnFilterChanged()
    {
        if (_set is { } set)
        {
            var boards = new Dictionary<string, string>(_services.Settings.Current.SpeedrunComBoards)
            {
                [set.Id] = string.Join(";", Filters.Select(f => $"{f.Filter.Key}={f.Selected.Key}")),
            };
            _services.Settings.Save(_services.Settings.Current with { SpeedrunComBoards = boards });
        }

        _ = LoadAsync(refresh: false);
    }

    private async Task LoadAsync(bool refresh)
    {
        if (!Enabled || Board is not { } board || _set is not { } set || Filters.Count == 0)
        {
            return;
        }

        var load = ++_loads;
        Status = "Reading the leaderboard from speedrun.com…";
        try
        {
            var choices = Filters.ToDictionary(f => f.Filter.Key, f => f.Selected.Key);
            var result = await _services.SpeedrunCom.LeaderboardAsync(board, choices, refresh);
            if (load != _loads)
            {
                return; // a newer request is on its way
            }

            var user = _services.SpeedrunAccount.User;
            var mine = result.Best(user?.Id, user?.Name);
            Top = [.. result.Entries.Take(TopShown).Select(e => Row(e, mine)).Concat(
                mine is not null && mine.Place > TopShown ? [Row(mine, mine)] : [])];
            Record = result.Record is { } wr
                ? $"World record {RunRows.Clock(wr.Time)} by {wr.Runner}" + (wr.Date is { } d ? $" ({d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})" : string.Empty)
                : "No runs on this board yet.";
            var pb = _services.RunLibrary.Comparison(set).PersonalBest is { Count: > 0 } best ? best[^1] : null;
            YourPlace = string.Join(" · ", new[]
            {
                pb is { } p ? $"Your best here, {RunRows.Clock(p)}, would place #{result.PlaceFor(p)} of {result.Entries.Count + 1}" : null,
                mine is not null ? $"on the board: #{mine.Place} in {RunRows.Clock(mine.Time)}" : null,
            }.Where(s => s is not null));
            Weblink = result.Weblink ?? board.Weblink;
            Status = $"{result.Entries.Count} runs · read {result.FetchedAt.ToLocalTime():HH:mm}";
            _services.SpeedrunCom.Shown = result;
        }
        catch (SpeedrunComException ex)
        {
            if (load == _loads)
            {
                Status = ex.Message;
            }
        }
    }

    private void Clear(string status)
    {
        Top = [];
        Record = YourPlace = string.Empty;
        Status = status;
        _services.SpeedrunCom.Shown = null;
    }

    private static LeaderboardRow Row(LeaderboardEntry e, LeaderboardEntry? mine) => new($"#{e.Place}", e.Runner, RunRows.Clock(e.Time),
        e.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty, ReferenceEquals(e, mine));

    private static Dictionary<string, string> Parse(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
}
