using Lockpick.Api;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Logging;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.Recording;
using Lockpick.Core.Trainer;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>Composition root: builds logging, settings, the RE1 adapter and the state source.</summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly HighResolutionWaitStrategy? _wait;
    private readonly ILogger _log;
    private GlobalHotkey? _markerHotkey;
    private GamepadButtons? _gamepad;
    private GamepadBinding _markerButton = new(GamepadBinding.Off);
    private TaskCompletionSource<GamepadButton>? _binding;

    private AppServices(
        JsonLinesLoggerProvider logProvider,
        ILoggerFactory loggerFactory,
        SettingsStore settings,
        Re1Adapter adapter,
        IGameStateSource source,
        FileFingerprintProvider fingerprints,
        HighResolutionWaitStrategy? wait,
        string? replayPath,
        IProcessWriteHost? writeHost = null)
    {
        Updates = new UpdateService(settings, loggerFactory.CreateLogger("Updates"));
        LogProvider = logProvider;
        LoggerFactory = loggerFactory;
        Settings = settings;
        Adapter = adapter;
        Source = source;
        Fingerprints = fingerprints;
        _wait = wait;
        _log = loggerFactory.CreateLogger("Lockpick");
        SetMarkerHotkey(settings.Current.MarkerHotkey);
        SetMarkerButton(settings.Current.MarkerButton);
        Pause = new GamePause(settings, loggerFactory.CreateLogger("GamePause"));
        Pause.RecoverFromCrash();
        ReplayPath = replayPath;
        if (writeHost is not null)
        {
            Trainer = new TrainerService(source, writeHost, new TrainerActionLog(AppPaths.TrainerLog), loggerFactory.CreateLogger("Trainer"));
        }

        Challenges = new Core.Challenges.ChallengeService(source, Trainer, adapter.CreateChallengeRules);
        Runs = new Core.Runs.RunService(source, Trainer, adapter.CreateRunRules, new Core.Runs.RunHistoryStore(AppPaths.Runs));
        RunLibrary = new RunLibrary(Runs, adapter, Runs.History!, settings, loggerFactory.CreateLogger("Runs"));
        TrainerHotkeys = new TrainerHotkeys(_log);
        TrainerHotkeys.Pressed += (_, action) =>
        {
            switch (action)
            {
                case TrainerHotkeys.RunSplit:
                    Runs.Split();
                    break;
                case TrainerHotkeys.RunUndo:
                    Runs.Undo();
                    break;
                case TrainerHotkeys.RunSkip:
                    Runs.Skip();
                    break;
                case TrainerHotkeys.RunReset:
                    Runs.Reset();
                    break;
            }
        };
        foreach (var (action, key) in settings.Current.TrainerHotkeys)
        {
            TrainerHotkeys.Set(action, key, MarkerHotkeyName);
        }
    }

    /// <summary>The challenge the player chose (Challenges page, API).</summary>
    public Core.Challenges.ChallengeService Challenges { get; }

    /// <summary>Run timing and splits (Runs page, overlay, API).</summary>
    public Core.Runs.RunService Runs { get; }

    /// <summary>The split sets, attempt records and comparisons.</summary>
    public RunLibrary RunLibrary { get; }

    /// <summary>System-wide keys for trainer actions (Settings); presses are acted on by the Trainer page.</summary>
    public TrainerHotkeys TrainerHotkeys { get; }

    /// <summary>The API for other tools, once the app has given it capture control (<see cref="CreateApi"/>).</summary>
    public ApiServer? Api { get; private set; }

    /// <summary>Why the API isn't listening although it is on (e.g. the port is taken), or null.</summary>
    public string? ApiError { get; private set; }

    public void CreateApi(ICaptureControl capture)
    {
        Api ??= new ApiServer(new ApiHost(Source, Adapter, Trainer, capture, AppVersion, Challenges), LoggerFactory.CreateLogger("Api"));
    }

    /// <summary>Starts, restarts or stops the API to match the settings (making a token the first time).</summary>
    public async Task ApplyApiSettingsAsync()
    {
        if (Api is null || IsReplay)
        {
            return;
        }

        var current = Settings.Current;
        if (!current.ApiEnabled)
        {
            ApiError = null;
            await Api.StopAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(current.ApiToken))
        {
            current = current with { ApiToken = ApiOptions.NewToken() };
            Settings.Save(current);
        }

        ApiError = await Api.StartAsync(new ApiOptions
        {
            AllowNetwork = current.ApiAllowNetwork,
            Port = current.ApiPort,
            Token = current.ApiToken!,
            AllowTrainer = current.ApiAllowTrainer,
        }).ConfigureAwait(false);
    }

    /// <summary>The trainer (live game only; null while replaying). Read-only until the player turns Trainer Mode on.</summary>
    public TrainerService? Trainer { get; }

    public JsonLinesLoggerProvider LogProvider { get; }

    public ILoggerFactory LoggerFactory { get; }

    public SettingsStore Settings { get; }

    public Re1Adapter Adapter { get; }

    public IGameStateSource Source { get; }

    public FileFingerprintProvider Fingerprints { get; }

    public UpdateService Updates { get; }

    /// <summary>Freezes the game while an ask-mode question is open.</summary>
    public GamePause Pause { get; }

    /// <summary>Raised (on a background thread) when the marker hotkey is pressed, whichever window has focus.</summary>
    public event EventHandler? MarkerHotkeyPressed;

    /// <summary>The registered marker hotkey's name, or null when it is off or Windows refused it.</summary>
    public string? MarkerHotkeyName => _markerHotkey is { IsRegistered: true } hotkey ? hotkey.Name : null;

    /// <summary>Replaces the marker hotkey; returns false if Windows refused it (another program may use it).</summary>
    public bool SetMarkerHotkey(string name)
    {
        _markerHotkey?.Dispose();
        _markerHotkey = GlobalHotkey.Create(name);
        if (_markerHotkey is null)
        {
            return name == "Off";
        }

        if (!_markerHotkey.IsRegistered)
        {
            _log.LogWarning("Marker hotkey {Key} is unavailable (error {Error}); another program may use it", name, _markerHotkey.Error);
            return false;
        }

        _markerHotkey.Pressed += (_, _) => MarkerHotkeyPressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The controller button that adds a marker (the same event as the hotkey).</summary>
    public GamepadBinding MarkerButton => _markerButton;

    /// <summary>Uses <paramref name="value"/> (a <see cref="GamepadBinding"/> value) for markers from the controller.</summary>
    public void SetMarkerButton(string value)
    {
        _markerButton = new GamepadBinding(value);
        EnsureGamepad();
    }

    /// <summary>The next controller button pressed (for binding one), or null after <paramref name="timeout"/>.</summary>
    public async Task<GamepadButton?> NextGamepadButtonAsync(TimeSpan timeout)
    {
        var wait = new TaskCompletionSource<GamepadButton>(TaskCreationOptions.RunContinuationsAsynchronously);
        _binding = wait;
        EnsureGamepad();
        try
        {
            return await wait.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _binding, null, wait);
            EnsureGamepad();
        }
    }

    /// <summary>Reads the controllers only while a button is bound or being bound.</summary>
    private void EnsureGamepad()
    {
        var needed = !_markerButton.IsOff || _binding is not null;
        if (needed && _gamepad is null)
        {
            _gamepad = new GamepadButtons();
            _gamepad.ButtonDown += OnGamepadButton;
            if (!_gamepad.HidAvailable)
            {
                _log.LogWarning("Raw Input is unavailable: only XInput controllers can add markers");
            }
        }
        else if (!needed && _gamepad is not null)
        {
            _gamepad.ButtonDown -= OnGamepadButton;
            _gamepad.Dispose();
            _gamepad = null;
        }
    }

    private void OnGamepadButton(object? sender, GamepadButton button)
    {
        if (_binding is { } binding)
        {
            binding.TrySetResult(button);
            return;
        }

        if (_markerButton.Matches(button))
        {
            MarkerHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string AppVersion => Updates.CurrentVersion;

    public string? ReplayPath { get; }

    public bool IsReplay => ReplayPath is not null;

    public bool HighResolutionTimer => _wait?.IsHighResolution ?? false;

    public static AppServices Create(string[] args)
    {
        var logPath = Path.Combine(AppPaths.Logs, $"lockpick-{DateTime.Now:yyyyMMdd}.jsonl");
        var logProvider = new JsonLinesLoggerProvider(logPath, args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Trace));
        var log = loggerFactory.CreateLogger("Lockpick");
        log.LogInformation("Lockpick {Version} starting", AppInfo.Version);

        var settings = new SettingsStore(AppPaths.SettingsFile, log);
        var current = settings.Load();

        var extraProfiles = ProfileLoader.LoadDirectory(
            current.ProfileDirectory ?? AppPaths.Profiles,
            (file, ex) => log.LogWarning("Ignoring invalid profile {File}: {Error}", file, ex.Message));
        var adapter = new Re1Adapter(extraProfiles);
        log.LogInformation("Loaded {Count} RE1 profile(s): {Ids}", adapter.Profiles.Count, string.Join(", ", adapter.Profiles.Select(p => p.Id)));

        var fingerprints = new FileFingerprintProvider();
        var replayIndex = Array.IndexOf(args, "--replay");
        if (replayIndex >= 0 && replayIndex + 1 < args.Length)
        {
            var path = args[replayIndex + 1];
            var (header, frames) = SessionRecording.ReadFile(path);
            log.LogInformation("Replay mode: {Path} ({Frames} frames)", path, frames.Count);
            return new AppServices(logProvider, loggerFactory, settings, adapter, new ReplaySource(header, frames, loop: true), fingerprints, null, path);
        }

        var wait = new HighResolutionWaitStrategy();
        var host = new Win32ProcessHost();
        var monitor = new GameMonitor(
            adapter,
            host,
            fingerprints,
            new MonitorOptions { WaitStrategy = wait },
            loggerFactory.CreateLogger<GameMonitor>());
        return new AppServices(logProvider, loggerFactory, settings, adapter, monitor, fingerprints, wait, null, host);
    }

    /// <summary>The executable to inspect offline: the configured path, else the GOG install.</summary>
    public string? ResolveInstalledExecutable() =>
        Settings.Current.GameExecutablePath is { Length: > 0 } configured
            ? configured
            : GogInstallLocator.Find(Re1Adapter.GogProductId)?.ExecutablePath;

    public async ValueTask DisposeAsync()
    {
        // Never back on the UI thread: App.Shutdown waits for this on it (resuming there hung the app on close).
        Pause.Dispose();
        if (Api is not null)
        {
            await Api.DisposeAsync().ConfigureAwait(false);
        }

        Challenges.Dispose();
        Runs.Dispose();
        Trainer?.Dispose();
        await Source.DisposeAsync().ConfigureAwait(false);
        Updates.Dispose();
        _markerHotkey?.Dispose();
        _gamepad?.Dispose();
        TrainerHotkeys.Dispose();
        _wait?.Dispose();
        LoggerFactory.Dispose();
        LogProvider.Dispose();
    }
}
