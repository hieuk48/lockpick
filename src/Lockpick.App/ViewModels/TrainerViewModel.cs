using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Trainer;

namespace Lockpick.App.ViewModels;

/// <summary>One line of the trainer's action log.</summary>
public sealed record TrainerActionRow(string Time, string Text, bool Applied)
{
    public static TrainerActionRow From(TrainerAction action) => new(
        action.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        action.Describe(),
        action.Result == TrainerResult.Applied);
}

/// <summary>
/// Trainer page. Lockpick starts read-only every time; the player chooses Trainer Mode (a write handle, validated
/// writes) or Challenge Mode (no write handle at all, the run kept clean). Everything here goes through
/// <see cref="TrainerService"/>, which checks, reads back and logs each write.
/// </summary>
public sealed partial class TrainerViewModel : PageViewModel
{
    private const string AmmoRule = TrainerRules.InfiniteAmmo;
    private const int MaxRows = 200;

    private readonly TrainerService? _trainer;
    private readonly AppServices _services;
    private bool _syncing;

    public TrainerViewModel(AppServices services, LiveState state)
        : base("Trainer", "Icon.Wrench")
    {
        _services = services;
        _trainer = services.Trainer;
        State = state;
        if (_trainer is not null)
        {
            foreach (var action in _trainer.Log.Recent.Take(MaxRows))
            {
                Actions.Add(TrainerActionRow.From(action));
            }

            _trainer.Log.Added += (_, a) => Dispatcher.UIThread.Post(() => AddRow(a));
            _trainer.StateChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
            _trainer.EventRaised += (_, e) => state.Add(e);
            services.TrainerHotkeys.Pressed += (_, action) => Dispatcher.UIThread.Post(() => OnHotkey(action));
        }

        state.PropertyChanged += OnStateChanged;
        Sync();
    }

    public LiveState State { get; }

    public bool Available => _trainer is not null;

    public ObservableCollection<TrainerActionRow> Actions { get; } = [];

    public string? LogPath => _trainer?.Log.Path;

    [ObservableProperty]
    public partial TrainerMode Mode { get; private set; }

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanWrite { get; private set; }

    [ObservableProperty]
    public partial string RunText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool RunModified { get; private set; }

    [ObservableProperty]
    public partial string HealthText { get; private set; } = "—";

    [ObservableProperty]
    public partial decimal? HealthTarget { get; set; }

    [ObservableProperty]
    public partial decimal HealthMax { get; private set; } = 1;

    [ObservableProperty]
    public partial bool HealthLocked { get; set; }

    [ObservableProperty]
    public partial bool InfiniteAmmo { get; set; }

    [ObservableProperty]
    public partial string LastResult { get; private set; } = string.Empty;

    public bool IsReadOnly => Mode == TrainerMode.ReadOnly;

    public bool IsTrainer => Mode == TrainerMode.Trainer;

    public bool IsChallenge => Mode == TrainerMode.Challenge;

    [RelayCommand]
    private void UseReadOnly() => _trainer?.SetMode(TrainerMode.ReadOnly);

    [RelayCommand]
    private void UseTrainer() => _trainer?.SetMode(TrainerMode.Trainer);

    [RelayCommand]
    private void UseChallenge() => _trainer?.SetMode(TrainerMode.Challenge);

    [RelayCommand]
    private void SetHealth()
    {
        if (_trainer is not null && Game() is { } game && HealthTarget is { } target)
        {
            Show(HealthLocked ? _trainer.Lock(game.HealthField, (long)target) : _trainer.Write(game.HealthField, (long)target));
        }
    }

    [RelayCommand]
    private void FillHealth()
    {
        if (_trainer is not null && Game() is { } game && State.Snapshot?.Health is { HasValue: true, Value.Maximum: { } max })
        {
            HealthTarget = max;
            Show(HealthLocked ? _trainer.Lock(game.HealthField, max) : _trainer.Write(game.HealthField, max));
        }
    }

    partial void OnHealthLockedChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (!value)
        {
            _trainer.Unlock(game.HealthField);
            return;
        }

        var target = HealthTarget ?? (State.Snapshot?.Health is { HasValue: true, Value: var health } ? health.Current : null);
        if (target is { } t)
        {
            Show(_trainer.Lock(game.HealthField, (long)t));
        }

        Sync();
    }

    partial void OnInfiniteAmmoChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (value)
        {
            Show(_trainer.SetRule(AmmoRule, "Infinite ammo", game.CreateInfiniteAmmoRule()));
        }
        else
        {
            _trainer.ClearRule(AmmoRule);
        }

        Sync();
    }

    partial void OnModeChanged(TrainerMode value)
    {
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsTrainer));
        OnPropertyChanged(nameof(IsChallenge));
    }

    /// <summary>A trainer hotkey: ignored unless Trainer Mode has write access (nothing happens, nothing is logged).</summary>
    private void OnHotkey(string action)
    {
        Sync();
        if (_trainer is null || !CanWrite || Game() is not { } game)
        {
            return;
        }

        switch (action)
        {
            case TrainerHotkeys.FillHealth when State.Snapshot?.Health is { HasValue: true, Value.Maximum: { } max }:
                HealthTarget = max;
                Show(HealthLocked ? _trainer.Lock(game.HealthField, max, TrainerSource.Hotkey) : _trainer.Write(game.HealthField, max, TrainerSource.Hotkey));
                break;
            case TrainerHotkeys.ToggleHealthLock:
                if (HealthLocked)
                {
                    _trainer.Unlock(game.HealthField, TrainerSource.Hotkey);
                }
                else if ((HealthTarget ?? (State.Snapshot?.Health is { HasValue: true, Value: var health } ? health.Current : null)) is { } target)
                {
                    Show(_trainer.Lock(game.HealthField, (long)target, TrainerSource.Hotkey));
                }

                break;
            case TrainerHotkeys.ToggleInfiniteAmmo:
                if (InfiniteAmmo)
                {
                    _trainer.ClearRule(AmmoRule, TrainerSource.Hotkey);
                }
                else
                {
                    Show(_trainer.SetRule(AmmoRule, "Infinite ammo", game.CreateInfiniteAmmoRule(), TrainerSource.Hotkey));
                }

                break;
        }

        Sync();
    }

    private IGameTrainer? Game() =>
        State.Status.Profile is { } profile ? _services.Adapter.CreateTrainer(profile) : null;

    private void Show(TrainerAction action) =>
        LastResult = action.Result == TrainerResult.Applied ? $"Done: {action.Describe()}" : action.Describe();

    private void AddRow(TrainerAction action)
    {
        Actions.Insert(0, TrainerActionRow.From(action));
        while (Actions.Count > MaxRows)
        {
            Actions.RemoveAt(Actions.Count - 1);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveState.Snapshot) or nameof(LiveState.Status))
        {
            Sync();
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (_trainer is null)
            {
                ModeText = "The trainer isn't available while replaying a recording.";
                return;
            }

            Mode = _trainer.Mode;
            CanWrite = Mode == TrainerMode.Trainer && _trainer.HasWriteAccess;
            var attached = State.Status.State == ConnectionState.Attached;
            ModeText = Mode switch
            {
                TrainerMode.ReadOnly => "Read-only: Lockpick only reads the game. It has no way to change it.",
                TrainerMode.Challenge => "Challenge Mode: Lockpick has no write access and refuses every change, so this run stays as you played it.",
                _ when _trainer.HasWriteAccess => "Trainer Mode: Lockpick may change the game. Every change is checked, read back and logged.",
                _ when _trainer.WriteAccessError is { } error => $"Trainer Mode, but Windows refused write access: {error}",
                _ when !attached => "Trainer Mode: waiting for the game.",
                _ => "Trainer Mode: no write access yet.",
            };

            RunModified = _trainer.RunModified;
            RunText = _trainer.RunModifiedAt is { } at
                ? $"This run was changed by the trainer (first change at {at.ToLocalTime():HH:mm:ss}). A new game or a loaded save starts a clean run."
                : "This run hasn't been changed by the trainer.";

            if (State.Snapshot?.Health is { HasValue: true, Value: var health })
            {
                HealthText = health.Maximum is { } max ? $"{health.Current} / {max}" : $"{health.Current}";
                HealthMax = Math.Max(1, health.Maximum ?? health.Current);
                HealthTarget ??= health.Maximum ?? health.Current;
            }
            else
            {
                HealthText = "—";
            }

            var game = Game();
            HealthLocked = game is not null && _trainer.Locks.ContainsKey(game.HealthField);
            InfiniteAmmo = _trainer.HasRule(AmmoRule);
        }
        finally
        {
            _syncing = false;
        }
    }
}
