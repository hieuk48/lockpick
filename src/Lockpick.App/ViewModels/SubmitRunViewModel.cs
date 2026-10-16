using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Runs;

namespace Lockpick.App.ViewModels;

/// <summary>A finished attempt the player can submit.</summary>
public sealed record SubmittableAttempt(RunRecord Record, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Runs → Submit: sends a finished attempt to speedrun.com as the player (with their kept API key). Everything is
/// filled in from the attempt and the leaderboard card, the player adds the video and checks it, and nothing is sent
/// until they press Submit twice. Attempts with the trainer on or from a save can't be submitted. The moderators
/// verify the run from the video; Lockpick never marks it verified.
/// </summary>
public sealed partial class SubmitRunViewModel : ObservableObject
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(8);

    private readonly AppServices _services;
    private readonly LeaderboardViewModel _board;
    private DateTimeOffset? _armedAt;
    private (string? Set, int Finished) _shown = ("-", -1);

    public SubmitRunViewModel(AppServices services, LeaderboardViewModel board)
    {
        _services = services;
        _board = board;
        services.SpeedrunAccount.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(HasKey)));
    }

    public bool HasKey => _services.SpeedrunAccount.HasKey;

    public string Account => _services.SpeedrunAccount.User?.Name ?? "your account";

    public System.Collections.ObjectModel.ObservableCollection<SubmittableAttempt> Attempts { get; } = [];

    [ObservableProperty]
    public partial SubmittableAttempt? Attempt { get; set; }

    [ObservableProperty]
    public partial bool HasAttempts { get; private set; }

    /// <summary>The time to submit (the in-game time on the end screen), h:mm:ss.ff.</summary>
    [ObservableProperty]
    public partial string Time { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Date { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Video { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Comment { get; set; } = string.Empty;

    /// <summary>Which board it goes on (changed in the Leaderboard card).</summary>
    [ObservableProperty]
    public partial string BoardText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SubmitLabel { get; private set; } = "Submit to speedrun.com…";

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? SubmittedLink { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool Busy { get; private set; }

    /// <summary>The splits changed or an attempt ended: the finished attempts from a new game, newest first.</summary>
    public void Show(SplitSet? set)
    {
        var finished = set is null ? [] : _services.RunLibrary.Records(set).Where(r => r.Result == RunResult.Finished).Reverse().ToList();
        BoardText = string.Join(" · ", _board.Filters.Select(f => f.Selected.Label));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(Account));
        if ((set?.Id, finished.Count) == _shown)
        {
            return; // nothing new: keep what the player has typed
        }

        _shown = (set?.Id, finished.Count);
        Attempts.Clear();
        if (set is not null)
        {
            foreach (var record in finished)
            {
                Attempts.Add(new SubmittableAttempt(record, $"#{record.Attempt} · {RunRows.Clock(record.GameTime)} · {record.StartedAt.ToLocalTime():yyyy-MM-dd}"
                    + (record.CheatsUsed ? " · trainer on" : string.Empty) + (record.FromSave ? " · from a save" : string.Empty)));
            }
        }

        HasAttempts = Attempts.Count > 0;
        Attempt = Attempts.FirstOrDefault();
    }

    partial void OnAttemptChanged(SubmittableAttempt? value)
    {
        _armedAt = null;
        SubmitLabel = "Submit to speedrun.com…";
        SubmittedLink = null;
        if (value is not { Record: var record })
        {
            return;
        }

        Time = Exact(record.GameTime);
        Date = record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Comment = record.Summary("Resident Evil (PC)", $"Lockpick {_services.AppVersion}");
        Status = record.CheatsUsed ? "This attempt had Lockpick's trainer on: the leaderboard doesn't allow it, so it can't be submitted."
            : record.FromSave ? "This attempt started from a save: leaderboard runs start at the title, so it can't be submitted."
            : string.Empty;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SubmitAsync()
    {
        BoardText = string.Join(" · ", _board.Filters.Select(f => f.Selected.Label));
        if (Problem() is { } problem)
        {
            Status = problem;
            _armedAt = null;
            SubmitLabel = "Submit to speedrun.com…";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_armedAt is not { } armed || now - armed > ConfirmWindow)
        {
            // First press: say exactly what will happen; the second press within a few seconds sends it.
            _armedAt = now;
            SubmitLabel = $"Press again to submit as {Account}";
            Status = $"This sends {Time} on {BoardText} to speedrun.com as {Account}, for the moderators to check against your video.";
            return;
        }

        _armedAt = null;
        SubmitLabel = "Submit to speedrun.com…";
        Busy = true;
        Status = "Sending the run to speedrun.com…";
        try
        {
            var board = _board.Board!;
            var run = new RunSubmission(board.Category, board.Platform,
                _board.Filters.ToDictionary(f => f.Filter.VariableId, f => f.Selected.Id), board.TimeField,
                ParseTime(Time)!.Value, DateOnly.ParseExact(Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture), new Uri(Video.Trim()), Comment);
            var submitted = await _services.SpeedrunAccount.SubmitAsync(run);
            SubmittedLink = submitted.Weblink;
            Status = "Submitted. It's waiting for the moderators to check it" + (submitted.Weblink is null ? "." : ": open it below.");
        }
        catch (SpeedrunComException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private bool NotBusy() => !Busy;

    /// <summary>Why the run can't be sent as it is, or null.</summary>
    private string? Problem()
    {
        if (!HasKey)
        {
            return "Save your speedrun.com API key first (Settings → speedrun.com).";
        }

        if (Attempt is not { Record: var record })
        {
            return "Finish an attempt first: only finished runs can be submitted.";
        }

        if (record.CheatsUsed || record.FromSave)
        {
            return Status;
        }

        if (_board.Board is null || _board.Filters.Count == 0)
        {
            return "Pick the leaderboard in the Leaderboard card first.";
        }

        if (ParseTime(Time) is not { } time || time <= TimeSpan.Zero)
        {
            return "The time doesn't read as h:mm:ss.ff (e.g. 0:38:01.50).";
        }

        if (!DateOnly.TryParseExact(Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day > DateOnly.FromDateTime(DateTime.Today))
        {
            return "The date should be the day you played, as yyyy-mm-dd.";
        }

        if (!Uri.TryCreate(Video.Trim(), UriKind.Absolute, out var video) || (video.Scheme != Uri.UriSchemeHttps && video.Scheme != Uri.UriSchemeHttp))
        {
            return "Add the link to your video (speedrun.com requires one for Resident Evil): a YouTube or Twitch link, for example.";
        }

        return null;
    }

    private static TimeSpan? ParseTime(string text)
    {
        var formats = new[] { @"h\:mm\:ss\.FFF", @"h\:mm\:ss", @"m\:ss\.FFF", @"m\:ss" };
        return TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out var time) ? time : null;
    }

    private static string Exact(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}");
}
