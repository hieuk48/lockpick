using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Classic REbirth in an install: its version (from the DLL's hash) and its settings (<c>config.ini</c>).</summary>
/// <param name="Version">"1.1.4" etc., or null for a REbirth DLL Lockpick doesn't know.</param>
/// <param name="Settings">config.ini as section → key → value (empty until REbirth has run once).</param>
/// <param name="ModFolders">REbirth mod folders (<c>mod_*</c>) beside the game.</param>
public sealed record RebirthInstall(string? Version, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings, IReadOnlyList<string> ModFolders)
{
    /// <summary>A [DLL] option that is on (1).</summary>
    public bool On(string key) =>
        Settings.TryGetValue("DLL", out var dll) && dll.TryGetValue(key, out var value) && value.Trim() == "1";
}

/// <summary>
/// What an RE1 install folder holds, read-only: Classic REbirth (which replaces GOG's DirectDraw wrapper and keeps its
/// own settings and saves), where saves and room files are. GOG's English release: SAVE\ and USA\, settings in
/// dxcfg.ini. The Japanese 1.01 game with REbirth: savedata\ (REbirth's Save_folder) and JPN\, settings in config.ini.
/// </summary>
public sealed record Re1Install(string Directory, RebirthInstall? Rebirth)
{
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> Hashes = new();

    /// <summary>The published Classic REbirth releases and the executable it runs on (data/games/re1/install/rebirth.json).</summary>
    internal static RebirthData Known => KnownData.Value;

    public static Re1Install Inspect(string directory)
    {
        var dll = Path.Combine(directory, "ddraw.dll");
        var config = Path.Combine(directory, "config.ini");
        var version = Hash(dll) is { } digest ? Known.RebirthVersion(digest) : null;
        var settings = ReadIni(config);
        var isRebirth = version is not null || settings.ContainsKey("DLL");
        if (!isRebirth)
        {
            return new Re1Install(directory, null);
        }

        IReadOnlyList<string> mods;
        try
        {
            mods = [.. System.IO.Directory.EnumerateDirectories(directory, "mod_*").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mods = [];
        }

        return new Re1Install(directory, new RebirthInstall(version, settings, mods));
    }

    /// <summary>Where the game saves: REbirth's Save_folder (savedata by default), otherwise GOG's SAVE.</summary>
    public string SaveDirectory =>
        Rebirth is { } rebirth
            ? Path.Combine(Directory, rebirth.Settings.TryGetValue("RE1", out var re1) && re1.TryGetValue("Save_folder", out var folder) && folder.Trim().Length > 0
                ? folder.Trim()
                : "savedata")
            : Path.Combine(Directory, "SAVE");

    /// <summary>The room files' folder: USA (English data) or JPN (Japanese data).</summary>
    public string DataDirectory =>
        DataFolders.Select(d => Path.Combine(Directory, d)).FirstOrDefault(System.IO.Directory.Exists) ?? Directory;

    private static readonly string[] DataFolders = ["USA", "JPN"];

    /// <summary>GOG's DirectDraw wrapper settings, which apply only when REbirth hasn't replaced the wrapper.</summary>
    public string? GogDisplaySettings => Rebirth is null ? Path.Combine(Directory, "dxcfg.ini") : null;

    /// <summary>SHA-256 of a file (cached by size and time written), or null when it can't be read.</summary>
    internal static string? Hash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return Hashes.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), key =>
            {
                using var stream = File.OpenRead(key.Path);
                return Convert.ToHexString(SHA256.HashData(stream));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadIni(string path)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return sections;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return sections;
        }

        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line[1..^1].Trim()] = current;
            }
            else if (current is not null && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq && !line.StartsWith(';'))
            {
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return sections;
    }

    private static readonly Lazy<RebirthData> KnownData = new(() =>
    {
        using var stream = typeof(Re1Install).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.rebirth.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data rebirth.json.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        static Dictionary<string, string> Map(JsonElement list, string value) =>
            list.EnumerateArray().ToDictionary(e => e.GetProperty("sha256").GetString()!, e => e.GetProperty(value).GetString()!, StringComparer.OrdinalIgnoreCase);
        return new RebirthData(Map(root.GetProperty("executables"), "release"), Map(root.GetProperty("rebirth"), "version"),
            Map(root.GetProperty("rebirthFiles"), "file"));
    });
}

/// <summary>The hashes in rebirth.json: official executables (→ release), REbirth DLLs (→ version), REbirth's other files.</summary>
internal sealed record RebirthData(
    IReadOnlyDictionary<string, string> Executables,
    IReadOnlyDictionary<string, string> Dlls,
    IReadOnlyDictionary<string, string> Files)
{
    public string? RebirthVersion(string sha256) => Dlls.TryGetValue(sha256, out var version) ? version : null;
}
