using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lockpick.App.Services;

/// <summary>
/// One-click support bundle (#63): the last few days' logs, the settings without secrets, the game build Lockpick
/// saw, and Lockpick's and Windows' versions, zipped into the data folder's <c>support</c> folder to attach to an issue.
/// Never game files, saves, captures or the speedrun.com key (which isn't in the settings anyway); the API token is
/// left out and the Windows user folder is replaced by <c>%USERPROFILE%</c> everywhere.
/// </summary>
public static class SupportBundle
{
    private const int LogDays = 3;

    /// <summary>Settings that are secrets or personal: left out of the bundle.</summary>
    private static readonly string[] Secret = ["apiToken"];

    /// <summary>Makes the bundle; returns its path.</summary>
    public static string Create(AppServices services)
    {
        var folder = Path.Combine(AppPaths.DataRoot, "support");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Lockpick-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "about.txt", string.Join('\n',
                $"Lockpick {services.AppVersion}",
                $"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}",
                $".NET {Environment.Version}",
                $"Made {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
                $"Game: {services.Source.Status.Profile?.Id ?? "not attached now"}",
                $"Connection: {services.Source.Status.State} — {services.Source.Status.Message}") + "\n");
            Add(zip, "settings.json", CleanSettings());
            foreach (var log in RecentLogs())
            {
                Add(zip, "logs/" + Path.GetFileName(log), Read(log));
            }
        }

        return path;
    }

    /// <summary>The settings file without its secrets, as JSON.</summary>
    internal static string CleanSettings()
    {
        try
        {
            if (JsonNode.Parse(Read(AppPaths.SettingsFile)) is JsonObject settings)
            {
                foreach (var key in Secret)
                {
                    if (settings.ContainsKey(key))
                    {
                        settings[key] = "(left out)";
                    }
                }

                return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"(the settings couldn't be read: {ex.Message})";
        }

        return "(no settings yet)";
    }

    /// <summary>The Windows user folder replaced, so the bundle doesn't carry the user name.</summary>
    internal static string Anonymize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length == 0)
        {
            return text;
        }

        return text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace("\\", "\\\\", StringComparison.Ordinal), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> RecentLogs()
    {
        var logs = AppPaths.Logs;
        return Directory.Exists(logs)
            ? Directory.EnumerateFiles(logs, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(LogDays)
            : [];
    }

    /// <summary>Reads a file that Lockpick may be writing to at the same time.</summary>
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(Anonymize(text));
    }
}
