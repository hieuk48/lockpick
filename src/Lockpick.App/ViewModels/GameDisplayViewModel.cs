using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Display;
using Lockpick.Core.Monitoring;
using Lockpick.Platform.Windows;

namespace Lockpick.App.ViewModels;

/// <summary>A labelled choice in a list.</summary>
public sealed record DisplayChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The game's display settings from Lockpick (Settings → Game display, #20): windowed / borderless / fullscreen, the
/// resolution (only what the main monitor offers — the wrapper falls back to the desktop size otherwise), and how the
/// picture is scaled. Writes the game's settings file only when the player presses Apply, after backing it up once;
/// takes effect the next time the game starts. Also moves the game's window to another monitor (it can't be dragged).
/// </summary>
public sealed partial class GameDisplayViewModel : ObservableObject
{
    public const string Windowed = "windowed";
    public const string Borderless = "borderless";
    public const string Fullscreen = "fullscreen";

    private const string RestartLabel = "Restart Resident Evil";
    private const string RestartConfirmLabel = "Click again to restart — unsaved progress is lost";

    private readonly AppServices _services;
    private IGameDisplayConfig? _config;
    private int _armed;
    private IReadOnlyList<MonitorDescription> _monitors = [];
    private bool _loading;

    public GameDisplayViewModel(AppServices services)
    {
        _services = services;
        Load();
    }

    public IReadOnlyList<DisplayChoice> ModeChoices { get; } =
    [
        new("Windowed", Windowed),
        new("Borderless — fills the screen (best for the overlay and streaming)", Borderless),
        new("Fullscreen", Fullscreen),
    ];

    public ObservableCollection<DisplayChoice> ResolutionChoices { get; } = [];

    public IReadOnlyList<DisplayChoice> ScalingChoices { get; } =
    [
        new("Keep 4:3 (fit)", nameof(PictureScaling.Fit)),
        new("Whole multiples of 640×480 (sharpest)", nameof(PictureScaling.WholeMultiple)),
        new("Stretch to fill", nameof(PictureScaling.Stretch)),
    ];

    public ObservableCollection<MonitorDescription> MonitorChoices { get; } = [];

    /// <summary>The game's other display options (vsync, gamma, filtering, antialiasing for RE1).</summary>
    public ObservableCollection<DisplayOptionRow> Options { get; } = [];

    /// <summary>False when the game's settings file can't be found (no install found yet).</summary>
    [ObservableProperty]
    public partial bool Available { get; private set; }

    [ObservableProperty]
    public partial DisplayChoice? Mode { get; set; }

    [ObservableProperty]
    public partial DisplayChoice? Resolution { get; set; }

    [ObservableProperty]
    public partial DisplayChoice? Scaling { get; set; }

    [ObservableProperty]
    public partial bool CanChooseResolution { get; private set; }

    [ObservableProperty]
    public partial string Warning { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool HasBackup { get; private set; }

    /// <summary>True after Apply / Restore while the game runs with its old settings (it reads them when it starts).</summary>
    [ObservableProperty]
    public partial bool NeedsRestart { get; private set; }

    [ObservableProperty]
    public partial string RestartText { get; private set; } = RestartLabel;

    [ObservableProperty]
    public partial MonitorDescription? MoveTarget { get; set; }

    [ObservableProperty]
    public partial string MoveStatus { get; private set; } = string.Empty;

    private MonitorDescription? Main => _monitors.FirstOrDefault(m => m.Primary) ?? (_monitors.Count > 0 ? _monitors[0] : null);

    [RelayCommand]
    private void Apply()
    {
        if (_config is null || Mode is null || Scaling is null)
        {
            return;
        }

        var presentation = Mode.Value == Fullscreen ? DisplayPresentation.Fullscreen : DisplayPresentation.Windowed;
        var resolution = Mode.Value == Borderless ? DisplayConfiguration.Desktop : Resolution?.Value ?? DisplayConfiguration.Desktop;
        try
        {
            _config.Write(new DisplayConfiguration(presentation, resolution, Enum.Parse<PictureScaling>(Scaling.Value)));

            // Only options the player changed: an untouched default isn't added to the file.
            var changed = Options.Where(o => o.Selected is { } s && s.Value != o.Loaded).ToDictionary(o => o.Key, o => o.Selected!.Value);
            if (changed.Count > 0)
            {
                _config.WriteOptions(changed);
                ReloadOptions();
            }

            HasBackup = _config.HasBackup;
            NeedsRestart = _services.Source.Status.State == ConnectionState.Attached;
            Status = NeedsRestart
                ? "Saved. The game reads these settings when it starts — restart it to use them."
                : "Saved. Resident Evil uses it the next time it starts.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save {_config.Path}: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasBackup))]
    private void Restore()
    {
        if (_config is null)
        {
            return;
        }

        try
        {
            _config.RestoreBackup();
            Load();
            NeedsRestart = _services.Source.Status.State == ConnectionState.Attached;
            Status = NeedsRestart ? "Your original settings are back — restart the game to use them." : "Your original settings are back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't restore {_config.Path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Restarts the game so it reads the new settings. The first click arms it (anything since the last typewriter save
    /// is lost), a second click within 5 seconds restarts — no dialog.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)] // the confirming click comes while the first one waits
    private async Task RestartGameAsync()
    {
        var status = _services.Source.Status;
        if (status.State != ConnectionState.Attached || status.Process is not { } process)
        {
            NeedsRestart = false;
            Status = "Resident Evil isn't running; it uses the new settings when you start it.";
            return;
        }

        if (RestartText == RestartLabel)
        {
            RestartText = RestartConfirmLabel;
            var armed = ++_armed;
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (armed == _armed && RestartText == RestartConfirmLabel)
            {
                RestartText = RestartLabel; // not confirmed in time
            }

            return;
        }

        _armed++;
        RestartText = RestartLabel;
        if ((process.ExecutablePath ?? _services.ResolveInstalledExecutable()) is not { } executable)
        {
            Status = "The game's location isn't known — set it under Game installation.";
            return;
        }

        NeedsRestart = false;
        Status = "Restarting Resident Evil…";
        var problem = await GameProcess.RestartAsync(process.ProcessId, executable, _services.Adapter.WindowClass, TimeSpan.FromSeconds(5));
        Status = problem ?? "Restarted with the new settings.";
        NeedsRestart = problem is not null;
    }

    [RelayCommand]
    private void MoveGame()
    {
        var status = _services.Source.Status;
        if (MoveTarget is null || status.State != ConnectionState.Attached || status.Process is not { } process)
        {
            MoveStatus = "Start Resident Evil first.";
            return;
        }

        var window = GameWindows.Find(process.ProcessId, _services.Adapter.WindowClass);
        if (GameWindows.Read(window) is not { Mode: GameWindowMode.Windowed })
        {
            MoveStatus = "Only a windowed game can be moved (fullscreen and borderless fill their monitor).";
            return;
        }

        MoveStatus = Monitors.MoveWindowTo(window, MoveTarget) ? $"Moved to {MoveTarget}." : "Couldn't move the game window.";
    }

    /// <summary>Reads the current settings and the monitors again (the page shows them each time it opens).</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var target = MoveTarget?.Device;
            _monitors = Monitors.List();
            MonitorChoices.Clear();
            foreach (var m in _monitors)
            {
                MonitorChoices.Add(m);
            }

            // New items each time: pick the same monitor again by its device name.
            MoveTarget = _monitors.FirstOrDefault(m => m.Device == target) ?? _monitors.FirstOrDefault(m => !m.Primary) ?? Main;
            var exe = _services.ResolveInstalledExecutable();
            var directory = exe is null ? null : Path.GetDirectoryName(exe);
            _config = directory is null ? null : _services.Adapter.DisplayConfig(directory, AppPaths.DisplayBackup);
            Available = _config is not null && File.Exists(_config.Path);
            if (_config is null || !Available)
            {
                Status = "The game's display settings weren't found — set the game's location above.";
                return;
            }

            var current = _config.Read();
            HasBackup = _config.HasBackup;
            ReloadOptions();
            Mode = ModeChoices.First(c => c.Value == (current.Presentation == DisplayPresentation.Fullscreen ? Fullscreen
                : current.Resolution == DisplayConfiguration.Desktop ? Borderless : Windowed));
            Scaling = ScalingChoices.First(c => c.Value == current.Scaling.ToString());
            FillResolutions(current.Resolution);
        }
        finally
        {
            _loading = false;
            Check();
        }
    }

    private void ReloadOptions()
    {
        Options.Clear();
        if (_config is null)
        {
            return;
        }

        var values = _config.ReadOptions();
        foreach (var option in _config.Options)
        {
            Options.Add(new DisplayOptionRow(option, values.GetValueOrDefault(option.Key, option.Default)));
        }
    }

    partial void OnModeChanged(DisplayChoice? value)
    {
        if (!_loading)
        {
            FillResolutions(Resolution?.Value);
            Check();
        }
    }

    partial void OnResolutionChanged(DisplayChoice? value)
    {
        if (!_loading)
        {
            Check();
        }
    }

    private void FillResolutions(string? keep)
    {
        var main = Main;
        ResolutionChoices.Clear();
        var desktop = main is null ? string.Empty : $" ({main.Bounds.Width}×{main.Bounds.Height})";
        if (Mode?.Value == Fullscreen)
        {
            ResolutionChoices.Add(new DisplayChoice("Same as the desktop" + desktop, DisplayConfiguration.Desktop));
        }

        ResolutionChoices.Add(new DisplayChoice("The game's own (640×480)", DisplayConfiguration.GameOwn));
        foreach (var (w, h, hz) in main?.Modes ?? [])
        {
            // Windowed at the desktop's own size is the borderless choice; bigger than the desktop can't be windowed.
            if (Mode?.Value == Fullscreen || (main is not null && w <= main.Bounds.Width && h < main.Bounds.Height))
            {
                ResolutionChoices.Add(new DisplayChoice($"{w}×{h}", DisplayConfiguration.Mode(w, h, hz)));
            }
        }

        var keepSize = keep is null ? null : DisplayConfiguration.Size(keep);
        // The file's own value for a listed size stays as it is (e.g. "1920x1080@60" — not the monitor's fastest rate),
        // so Apply never changes what the player didn't.
        var index = keepSize is null ? -1 : ResolutionChoices.ToList().FindIndex(c => DisplayConfiguration.Size(c.Value) == keepSize);
        if (index >= 0 && keep is not null)
        {
            ResolutionChoices[index] = ResolutionChoices[index] with { Value = keep };
        }

        Resolution = ResolutionChoices.FirstOrDefault(c => c.Value == keep)
            ?? ResolutionChoices.FirstOrDefault(c => keepSize is not null && DisplayConfiguration.Size(c.Value) == keepSize)
            ?? ResolutionChoices.FirstOrDefault(c => c.Value == DisplayConfiguration.Desktop)
            ?? ResolutionChoices.FirstOrDefault();
        CanChooseResolution = Mode?.Value != Borderless;
    }

    private void Check()
    {
        var main = Main;
        var size = Resolution is null ? null : DisplayConfiguration.Size(Resolution.Value);
        Warning = Mode?.Value switch
        {
            Fullscreen when Resolution?.Value != DisplayConfiguration.Desktop =>
                "In fullscreen at a resolution other than your desktop's, your monitor switches resolution while you play — other windows can be rearranged. \"Same as the desktop\" avoids that.",
            Windowed when main is not null && size is { } s && s.Height + 32 > main.Work.Height =>
                "That window is taller than your screen's free area: its bottom will be hidden. Pick a smaller size, or Borderless.",
            _ => string.Empty,
        };
    }
}
