using Lockpick.Core.Adapters;
using Lockpick.Core.Runs;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// The split sets the player can run against (the game's built-in ones and LiveSplit files they imported, remembered
/// across restarts), every attempt's record, and what each set is compared with (the player's best here, else the
/// imported file's own). The run itself is timed by <see cref="RunService"/>.
/// </summary>
public sealed class RunLibrary
{
    private readonly RunService _runs;
    private readonly SettingsStore _settings;
    private readonly ILogger _log;
    private readonly List<SplitSet> _imported = [];
    private readonly List<SplitSet> _custom = [];
    private readonly string _customDirectory;
    private readonly Dictionary<string, RunComparison> _fileComparisons = [];
    private readonly Dictionary<string, RunComparison> _comparisons = [];
    private readonly object _gate = new();

    public RunLibrary(RunService runs, IGameAdapter adapter, RunHistoryStore history, SettingsStore settings, ILogger log, string? customDirectory = null)
    {
        _runs = runs;
        _settings = settings;
        _log = log;
        _customDirectory = customDirectory ?? Path.Combine(AppPaths.DataRoot, "splits", "mine");
        Adapter = adapter;
        History = history;
        Rules = adapter.Profiles.Select(adapter.CreateRunRules).FirstOrDefault(r => r is not null);
        BuiltIn = Rules?.SplitSets ?? [];
        foreach (var path in settings.Current.ImportedSplitFiles)
        {
            try
            {
                Add(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _log.LogWarning("Imported splits {Path} couldn't be read again: {Message}", path, ex.Message);
            }
        }

        if (Directory.Exists(_customDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_customDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    _custom.Add(SplitSetFile.Read(path));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    _log.LogWarning("Your splits {Path} couldn't be read: {Message}", path, ex.Message);
                }
            }
        }

        runs.StartOnLoad = settings.Current.RunStartOnLoad;
        runs.SaveRunsOnGameClock = settings.Current.RunSaveOnGameClock;
        runs.SaveStartSplit = set => _settings.Current.RunSaveStartSplits.GetValueOrDefault(set.Id);
        runs.AttemptEnded += (_, record) =>
        {
            lock (_gate)
            {
                _comparisons.Remove(record.SetId);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        };
        if (Find(settings.Current.RunSetId) is { } chosen)
        {
            runs.Choose(chosen);
        }
    }

    public RunHistoryStore History { get; }

    /// <summary>The game's run rules (split sets, its leaderboard), whether or not the game is running.</summary>
    public IRunRules? Rules { get; }

    public IReadOnlyList<SplitSet> BuiltIn { get; }

    /// <summary>The game (item and room names for the split editor).</summary>
    public IGameAdapter Adapter { get; }

    /// <summary>Built-in sets, then imported ones, then the player's own (the split editor).</summary>
    public IReadOnlyList<SplitSet> Sets
    {
        get
        {
            lock (_gate)
            {
                return [.. BuiltIn, .. _imported, .. _custom];
            }
        }
    }

    /// <summary>
    /// Saves <paramref name="set"/> as the player's own splits named <paramref name="name"/> (replacing their set of that
    /// name); returns it as saved. Built-in and imported sets are never changed: editing one makes the player's copy.
    /// </summary>
    public SplitSet SaveCustom(SplitSet set, string name)
    {
        var file = SplitSetFile.FileNameFor(name);
        var path = Path.Combine(_customDirectory, file + ".json");
        SplitSetFile.Write(path, set with { Name = name.Trim(), Category = set.Category });
        var saved = SplitSetFile.Read(path);
        lock (_gate)
        {
            _custom.RemoveAll(s => s.Id == saved.Id);
            _custom.Add(saved);
            _comparisons.Remove(saved.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// <summary>Raised when sets, records or comparisons change (any thread).</summary>
    public event EventHandler? Changed;

    public SplitSet? Find(string? id) => id is null ? null : Sets.FirstOrDefault(s => s.Id == id);

    /// <summary>Imports a LiveSplit file and remembers it; returns its split set. Throws InvalidDataException for a bad file.</summary>
    public SplitSet Import(string path)
    {
        var set = Add(path);
        var files = _settings.Current.ImportedSplitFiles.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Append(path).ToList();
        _settings.Save(_settings.Current with { ImportedSplitFiles = files });
        Changed?.Invoke(this, EventArgs.Empty);
        return set;
    }

    /// <summary>Forgets an imported set, or deletes the player's own (its attempts stay on disk).</summary>
    public void Remove(SplitSet set)
    {
        lock (_gate)
        {
            _imported.RemoveAll(s => s.Id == set.Id);
            if (_custom.RemoveAll(s => s.Id == set.Id) > 0)
            {
                try
                {
                    File.Delete(Path.Combine(_customDirectory, set.Id["mine-".Length..] + ".json"));
                }
                catch (IOException ex)
                {
                    _log.LogWarning("Your splits {Set} couldn't be deleted: {Message}", set.Id, ex.Message);
                }
            }
        }

        var files = _settings.Current.ImportedSplitFiles.Where(p => !string.Equals(p, set.ImportedFrom, StringComparison.OrdinalIgnoreCase)).ToList();
        _settings.Save(_settings.Current with { ImportedSplitFiles = files });
        if (_runs.Status.Set?.Id == set.Id)
        {
            Choose(null);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Times runs against <paramref name="set"/> (null: runs off) and remembers the choice.</summary>
    public void Choose(SplitSet? set)
    {
        if (set is null)
        {
            _runs.Stop();
        }
        else if (_runs.Status.Set?.Id != set.Id || _runs.Status.State == RunState.Off)
        {
            _runs.Choose(set);
        }

        if (_settings.Current.RunSetId != set?.Id)
        {
            _settings.Save(_settings.Current with { RunSetId = set?.Id });
        }
    }

    /// <summary>The split an attempt from a save starts at, for these splits.</summary>
    public int SaveStartSplit(SplitSet set) => _settings.Current.RunSaveStartSplits.GetValueOrDefault(set.Id);

    public void SetSaveStartSplit(SplitSet set, int index)
    {
        var splits = new Dictionary<string, int>(_settings.Current.RunSaveStartSplits) { [set.Id] = index };
        _settings.Save(_settings.Current with { RunSaveStartSplits = splits });
    }

    public void SetSaveOnGameClock(bool value)
    {
        _runs.SaveRunsOnGameClock = value;
        _settings.Save(_settings.Current with { RunSaveOnGameClock = value });
    }

    public void SetStartOnLoad(bool value)
    {
        _runs.StartOnLoad = value;
        _settings.Save(_settings.Current with { RunStartOnLoad = value });
    }

    /// <summary>The attempts against <paramref name="set"/>, oldest first.</summary>
    public IReadOnlyList<RunRecord> Records(SplitSet set)
    {
        try
        {
            return History.Load(set.Id);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Run history for {Set} couldn't be read: {Message}", set.Id, ex.Message);
            return [];
        }
    }

    /// <summary>The player's best here, filled from the imported file's own times where they have none yet.</summary>
    public RunComparison Comparison(SplitSet set)
    {
        lock (_gate)
        {
            if (_comparisons.TryGetValue(set.Id, out var known))
            {
                return known;
            }
        }

        var comparison = RunComparison.From(Records(set), set.Splits.Count);
        lock (_gate)
        {
            if (_fileComparisons.TryGetValue(set.Id, out var file))
            {
                comparison = comparison.Or(file);
            }

            _comparisons[set.Id] = comparison;
            return comparison;
        }
    }

    private SplitSet Add(string path)
    {
        var run = LiveSplitFile.Read(path);
        var set = LiveSplitFile.ToSplitSet(run, path, BuiltIn);
        lock (_gate)
        {
            _imported.RemoveAll(s => s.Id == set.Id);
            _imported.Add(set);
            if (run.Segments.Count == set.Splits.Count)
            {
                _fileComparisons[set.Id] = LiveSplitFile.ComparisonOf(run);
            }

            _comparisons.Remove(set.Id);
        }

        return set;
    }
}
