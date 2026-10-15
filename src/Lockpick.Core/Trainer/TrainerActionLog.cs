using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockpick.Core.Trainer;

/// <summary>
/// Every trainer action, kept on disk as JSON lines (one action per line) and the most recent ones in memory for the
/// app. Appends are synchronous and small; a failure to write the file is reported once and never stops the trainer.
/// </summary>
public sealed class TrainerActionLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly LinkedList<TrainerAction> _recent = new();
    private readonly int _keep;
    private bool _fileFailed;

    /// <param name="path">The JSON-lines file, or null to keep actions in memory only (tests, replays).</param>
    public TrainerActionLog(string? path, int keep = 500)
    {
        Path = path;
        _keep = keep;
    }

    public string? Path { get; }

    public event EventHandler<TrainerAction>? Added;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TrainerAction> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    public string? FileError { get; private set; }

    public void Add(TrainerAction action)
    {
        lock (_gate)
        {
            _recent.AddFirst(action);
            while (_recent.Count > _keep)
            {
                _recent.RemoveLast();
            }

            if (Path is not null && !_fileFailed)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    File.AppendAllText(Path, JsonSerializer.Serialize(action, Json) + "\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _fileFailed = true;
                    FileError = ex.Message;
                }
            }
        }

        Added?.Invoke(this, action);
    }
}
