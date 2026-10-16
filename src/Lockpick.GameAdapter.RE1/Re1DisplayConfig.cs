using Lockpick.Core.Display;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// GOG's DirectDraw wrapper settings (<c>dxcfg.ini</c>, docs/DISPLAY.md) as the player changes them from Lockpick: only
/// <c>presentation</c>, <c>display</c>, <c>aspect</c> and <c>scaling</c> are touched, every other line is kept. The file
/// as it was before Lockpick's first change is copied to the backup folder (outside the game folder) and can be put back.
/// </summary>
public sealed class Re1DisplayConfig(string iniPath, string backupDirectory) : IGameDisplayConfig
{
    private string BackupPath => System.IO.Path.Combine(backupDirectory, "dxcfg.ini");

    public string Path => iniPath;

    public bool HasBackup => File.Exists(BackupPath);

    public DisplayConfiguration Read()
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath) : [];
        string? Value(string key) => lines.Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).LastOrDefault();
        var presentation = Value("presentation") is { } p && p.Equals("fullscreen", StringComparison.OrdinalIgnoreCase)
            ? DisplayPresentation.Fullscreen
            : DisplayPresentation.Windowed;
        var display = Value("display") is { Length: > 0 } d ? d.ToLowerInvariant() : DisplayConfiguration.Desktop;
        return new DisplayConfiguration(presentation, display, Re1Display.Parse(lines).Scaling);
    }

    public void Write(DisplayConfiguration configuration)
    {
        if (!HasBackup && File.Exists(iniPath))
        {
            Directory.CreateDirectory(backupDirectory);
            File.Copy(iniPath, BackupPath);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["presentation"] = configuration.Presentation == DisplayPresentation.Fullscreen ? "fullscreen" : "windowed",
            ["display"] = configuration.Resolution,
            ["aspect"] = configuration.Scaling == PictureScaling.Stretch ? "disabled" : "enabled",
            ["scaling"] = configuration.Scaling == PictureScaling.WholeMultiple ? "integer" : "fit",
        };
        var lines = (File.Exists(iniPath) ? File.ReadAllLines(iniPath) : ["[dxcfg]"]).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split('=', 2);
            if (parts.Length == 2 && values.TryGetValue(parts[0].Trim(), out var value))
            {
                lines[i] = $"{parts[0].Trim()}={value}";
                seen.Add(parts[0].Trim());
            }
        }

        var section = lines.FindIndex(l => l.Trim().Equals("[dxcfg]", StringComparison.OrdinalIgnoreCase));
        var insertAt = section < 0 ? lines.Count : section + 1;
        foreach (var (key, value) in values.Where(v => !seen.Contains(v.Key)))
        {
            lines.Insert(insertAt++, $"{key}={value}");
        }

        File.WriteAllLines(iniPath, lines);
    }

    public void RestoreBackup()
    {
        if (HasBackup)
        {
            File.Copy(BackupPath, iniPath, overwrite: true);
        }
    }
}
