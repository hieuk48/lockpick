using System.Text.Json;
using System.Text.Json.Serialization;
using Lockpick.Core.State;

namespace Lockpick.Core.Runs;

/// <summary>
/// The player's own split sets (the split editor, #57), one JSON file each: names and triggers. Lockpick reads the
/// triggers back exactly as saved; nothing in the file is executed.
/// </summary>
public static class SplitSetFile
{
    /// <summary>What a custom set's <see cref="SplitSet.Source"/> says.</summary>
    public const string Custom = "yours";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(string path, SplitSet set)
    {
        var file = new FileSet(set.Name, set.Category, set.Character, [.. set.Splits.Select(ToFile)]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>A custom set from its file; its id comes from the file name.</summary>
    public static SplitSet Read(string path)
    {
        var file = JsonSerializer.Deserialize<FileSet>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The splits file is empty.");
        if (string.IsNullOrWhiteSpace(file.Name) || file.Splits is not { Count: > 0 } splits)
        {
            throw new InvalidDataException("A splits file needs a name and at least one split.");
        }

        return new SplitSet(IdFor(Path.GetFileNameWithoutExtension(path)), file.Name, file.Category ?? file.Name, file.Character,
            [.. splits.Select(FromFile)], Verified: false, Custom);
    }

    /// <summary>The id of a custom set saved as <paramref name="fileName"/>.</summary>
    public static string IdFor(string fileName) => "mine-" + fileName;

    /// <summary>A file name for a set called <paramref name="name"/> (letters, digits and dashes).</summary>
    public static string FileNameFor(string name)
    {
        var slug = string.Concat(name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "splits" : slug[..Math.Min(slug.Length, 60)];
    }

    private static FileSplit ToFile(SplitDefinition split) => split.Trigger switch
    {
        RoomEntered r => new FileSplit(split.Name, "room", r.Room, r.From, r.FromOtherStage ? true : null, null, null),
        ItemObtained i => new FileSplit(split.Name, "item", null, null, null, i.ItemId, i.Room),
        RunFinished => new FileSplit(split.Name, "ending", null, null, null, null, null),
        _ => new FileSplit(split.Name, "key", null, null, null, null, null),
    };

    private static SplitDefinition FromFile(FileSplit split)
    {
        var name = string.IsNullOrWhiteSpace(split.Name) ? "Split" : split.Name.Trim();
        SplitTrigger trigger = split.Kind switch
        {
            "room" when !string.IsNullOrWhiteSpace(split.Room) => new RoomEntered(split.Room.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(split.From) ? null : split.From.Trim().ToUpperInvariant(), split.OtherArea == true),
            "item" when split.Item is > 0 => new ItemObtained(split.Item.Value, string.IsNullOrWhiteSpace(split.ItemRoom) ? null : split.ItemRoom.Trim().ToUpperInvariant()),
            "ending" => new RunFinished(),
            _ => new ManualSplit(),
        };
        return new SplitDefinition(name, trigger);
    }

    private sealed record FileSet(string Name, string? Category, PlayableCharacter Character, IReadOnlyList<FileSplit>? Splits);

    private sealed record FileSplit(string Name, string Kind, string? Room, string? From, bool? OtherArea, int? Item, string? ItemRoom);
}
