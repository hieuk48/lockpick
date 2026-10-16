using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Challenges;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Challenges page, laid out as steps: choose a challenge, check its rules, start it, then see whether the attempt is
/// still clean. While a challenge runs Lockpick can't change the game (Challenge Mode).
/// </summary>
public sealed partial class ChallengesViewModel : PageViewModel
{
    private readonly ChallengeService _challenges;
    private readonly AppServices _services;
    private readonly bool _loading = true;

    public ChallengesViewModel(AppServices services, LiveState state)
        : base("Challenges", "Icon.Trophy")
    {
        _services = services;
        _challenges = services.Challenges;
        _challenges.EventRaised += (_, e) => state.Add(e);
        _challenges.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        services.Runs.Died += (_, _) => Dispatcher.UIThread.Post(Sync);
        AlertSound = services.Settings.Current.AlertSound;
        Selected = Presets[0];
        Sync();
        _loading = false;
    }

    /// <summary>"Deaths this session: 3" (any play since Lockpick started).</summary>
    [ObservableProperty]
    public partial string Deaths { get; private set; } = string.Empty;

    /// <summary>Windows' alert sound on a fail or a death.</summary>
    [ObservableProperty]
    public partial bool AlertSound { get; set; }

    partial void OnAlertSoundChanged(bool value)
    {
        if (!_loading)
        {
            _services.Settings.Save(_services.Settings.Current with { AlertSound = value });
        }
    }

    public IReadOnlyList<ChallengeDefinition> Presets => ChallengeDefinition.Presets;

    [ObservableProperty]
    public partial ChallengeDefinition? Selected { get; set; }

    [ObservableProperty]
    public partial bool NoDamage { get; set; }

    [ObservableProperty]
    public partial bool KnifeOnly { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    public partial string StatusTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusDetail { get; private set; } = string.Empty;

    /// <summary>Choosing is possible only while no challenge is running.</summary>
    public bool CanChoose => !IsActive;

    partial void OnSelectedChanged(ChallengeDefinition? value)
    {
        NoDamage = value?.Rules.Contains(ChallengeRule.NoDamage) ?? false;
        KnifeOnly = value?.Rules.Contains(ChallengeRule.KnifeOnly) ?? false;
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(CanChoose));

    [RelayCommand]
    private void Start()
    {
        if (Selected is not { } challenge)
        {
            return;
        }

        List<ChallengeRule> rules = [];
        if (NoDamage)
        {
            rules.Add(ChallengeRule.NoDamage);
        }

        if (KnifeOnly)
        {
            rules.Add(ChallengeRule.KnifeOnly);
        }

        _challenges.Start(challenge, rules);
    }

    [RelayCommand]
    private void Retry() => _challenges.Retry();

    [RelayCommand]
    private void Stop() => _challenges.Stop();

    private void Sync()
    {
        var deaths = _services.Runs.Deaths;
        Deaths = deaths == 0 ? "No deaths this session." : deaths == 1 ? "1 death this session." : $"{deaths} deaths this session.";
        var status = _challenges.Status;
        IsActive = status.State != ChallengeState.Off;
        IsRunning = status.State == ChallengeState.Running;
        IsFailed = status.State == ChallengeState.Failed;
        var name = status.Challenge?.Name;
        var rules = status.Rules.Count == 0 ? "no extra rules" : string.Join(" + ", status.Rules.Select(ChallengeDefinition.Describe));
        (StatusTitle, StatusDetail) = status.State switch
        {
            ChallengeState.Off => ("No challenge running",
                "Pick a challenge below and press Start. Lockpick can't change your game while it runs."),
            ChallengeState.Waiting => ($"{name}: ready",
                $"Start a new game or load a save in Resident Evil. The attempt begins when you're in the game ({rules})."),
            ChallengeState.Running => ($"{name}: attempt {status.Attempt} — still clean",
                $"Playing since {status.AttemptStarted?.ToLocalTime():HH:mm:ss} ({rules}). A new game or a loaded save starts a new attempt."),
            _ => ($"{name}: failed — {status.FailReason}",
                (status.FailedAtGameTime is { } t ? $"In-game time {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}. " : string.Empty)
                + "Press Try again, or start a new game or load a save for a new attempt."),
        };
    }
}
