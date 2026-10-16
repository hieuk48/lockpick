using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Lockpick.Core.State;

namespace Lockpick.Core.Runs;

/// <summary>One segment of a LiveSplit file: its name, the personal best's split time and the best segment.</summary>
public sealed record LiveSplitSegment(string Name, TimeSpan? PersonalBestGameTime, TimeSpan? PersonalBestRealTime, TimeSpan? BestSegmentGameTime);

/// <summary>A LiveSplit splits file (<c>.lss</c>), as far as Lockpick uses it.</summary>
public sealed record LiveSplitRun(string GameName, string CategoryName, IReadOnlyList<LiveSplitSegment> Segments, int AttemptCount);

/// <summary>
/// Reads and writes LiveSplit's <c>.lss</c> files, so players can bring the community's splits (or their own) and take
/// Lockpick's times to LiveSplit or a leaderboard submission. Reading is defensive: these are files from the internet.
/// </summary>
public static class LiveSplitFile
{
    private const int MaxSegments = 500;
    private const long MaxBytes = 64L * 1024 * 1024; // the community's own files with icons run to a few MB

    public static LiveSplitRun Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidDataException("That file is too big to be a LiveSplit splits file.");
        }

        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static LiveSplitRun Read(Stream stream)
    {
        // No DTDs or external entities: a downloaded file must not make Lockpick fetch or expand anything.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Not a LiveSplit splits file ({ex.Message}).", ex);
        }

        var run = document.Root is { Name.LocalName: "Run" } root ? root : throw new InvalidDataException("Not a LiveSplit splits file (no <Run>).");
        var segments = run.Element("Segments")?.Elements("Segment").Take(MaxSegments + 1).ToList() ?? [];
        if (segments.Count == 0)
        {
            throw new InvalidDataException("The file has no splits.");
        }

        if (segments.Count > MaxSegments)
        {
            throw new InvalidDataException($"The file has more than {MaxSegments} splits.");
        }

        return new LiveSplitRun(
            Text(run.Element("GameName")),
            Text(run.Element("CategoryName")),
            [.. segments.Select(s =>
            {
                var pb = s.Element("SplitTimes")?.Elements("SplitTime").FirstOrDefault(t => (string?)t.Attribute("name") == "Personal Best");
                return new LiveSplitSegment(
                    Text(s.Element("Name")),
                    Time(pb?.Element("GameTime")),
                    Time(pb?.Element("RealTime")),
                    Time(s.Element("BestSegmentTime")?.Element("GameTime")));
            })],
            int.TryParse(Text(run.Element("AttemptCount")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts) ? attempts : 0);
    }

    /// <summary>
    /// The file's splits as a split set. Each split takes the trigger of the same name from the built-in set that fits
    /// the file best (same character, most names in common, then the same category), else from any built-in set for
    /// the character, else the split key. Names differ in meaning between categories (e.g. Jill's "Basement" in the
    /// good and bad endings), hence the best-fitting set first.
    /// </summary>
    public static SplitSet ToSplitSet(LiveSplitRun run, string fileName, IReadOnlyList<SplitSet> builtIn)
    {
        var character = CharacterOf(run.CategoryName + " " + fileName);
        var names = run.Segments.Select(s => Normalize(s.Name)).ToHashSet();
        var candidates = builtIn.Where(s => character == PlayableCharacter.Unknown || s.Character == character).ToList();
        var best = candidates
            .OrderByDescending(s => s.Splits.Count(split => names.Contains(Normalize(split.Name))))
            .ThenByDescending(s => string.Equals(s.Category, run.CategoryName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        SplitTrigger? TriggerFor(string name) =>
            (best?.Splits.FirstOrDefault(s => Normalize(s.Name) == Normalize(name))
                ?? candidates.SelectMany(s => s.Splits).FirstOrDefault(s => Normalize(s.Name) == Normalize(name)))?.Trigger;
        var splits = run.Segments.Select(s => new SplitDefinition(s.Name, TriggerFor(s.Name) ?? new ManualSplit())).ToList();
        if (character == PlayableCharacter.Unknown && best is not null)
        {
            character = best.Character;
        }

        var category = string.IsNullOrWhiteSpace(run.CategoryName) ? Path.GetFileNameWithoutExtension(fileName) : run.CategoryName;
        return new SplitSet("lss-" + Slug(category + "-" + Path.GetFileNameWithoutExtension(fileName)), category, category, character, splits,
            Verified: false, Source: $"LiveSplit file {Path.GetFileName(fileName)}") { ImportedFrom = fileName };
    }

    /// <summary>The file's own personal best and best segments (in-game time), for comparison before there are runs here.</summary>
    public static RunComparison ComparisonOf(LiveSplitRun run) => new(
        [.. run.Segments.Select(s => s.PersonalBestGameTime)],
        [.. run.Segments.Select(s => s.BestSegmentGameTime)]);

    /// <summary>
    /// Writes <paramref name="set"/> as a LiveSplit file with the personal best and best segments from
    /// <paramref name="records"/> (in-game and real time) and every attempt in its history, so it opens in LiveSplit
    /// with the player's times.
    /// </summary>
    public static void Write(string path, string gameName, SplitSet set, IReadOnlyList<RunRecord> records)
    {
        var mine = records.Where(r => r.Splits.Count == set.Splits.Count).ToList();
        var best = mine.Where(r => r.IsClean).OrderBy(r => r.GameTime).FirstOrDefault();
        var comparison = RunComparison.From(mine, set.Splits.Count);
        var attempts = mine.Select((r, i) => (Id: i + 1, Record: r)).ToList();

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Run", new XAttribute("version", "1.7.0"),
                new XElement("GameIcon"),
                new XElement("GameName", gameName),
                new XElement("CategoryName", set.Category),
                new XElement("LayoutPath"),
                new XElement("Metadata", new XElement("Run", new XAttribute("id", string.Empty)), new XElement("Platform", new XAttribute("usesEmulator", "False")),
                    new XElement("Region"), new XElement("Variables")),
                new XElement("Offset", "00:00:00"),
                new XElement("AttemptCount", attempts.Count),
                new XElement("AttemptHistory", attempts.Select(a => new XElement("Attempt",
                    new XAttribute("id", a.Id),
                    new XAttribute("started", a.Record.StartedAt.UtcDateTime.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XAttribute("isStartedSynced", "True"),
                    new XAttribute("ended", a.Record.EndedAt.UtcDateTime.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XAttribute("isEndedSynced", "True"),
                    a.Record.Result == RunResult.Finished ? Times(a.Record.RealTime, a.Record.GameTime) : null))),
                new XElement("Segments", set.Splits.Select((split, i) => new XElement("Segment",
                    new XElement("Name", split.Name),
                    new XElement("Icon"),
                    new XElement("SplitTimes", new XElement("SplitTime", new XAttribute("name", "Personal Best"),
                        best is null ? [] : Times(best.Splits[i].RealTime, best.Splits[i].GameTime))),
                    new XElement("BestSegmentTime", comparison.BestSegments[i] is { } segment ? Times(null, segment) : []),
                    new XElement("SegmentHistory", attempts.Select(a => SegmentTime(a.Id, a.Record, i)).Where(e => e is not null))))),
                new XElement("AutoSplitterSettings")));

        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true) };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static XElement? SegmentTime(int id, RunRecord record, int index)
    {
        var end = record.Splits[index];
        var start = index == 0 ? new RecordedSplit(string.Empty, TimeSpan.Zero, TimeSpan.Zero) : record.Splits[index - 1];
        if (end.GameTime is not { } endGame || start.GameTime is not { } startGame)
        {
            return null;
        }

        var real = end.RealTime is { } endReal && start.RealTime is { } startReal ? endReal - startReal : (TimeSpan?)null;
        return new XElement("Time", new XAttribute("id", id), Times(real, endGame - startGame));
    }

    private static object?[] Times(TimeSpan? real, TimeSpan? game) =>
    [
        real is { } r ? new XElement("RealTime", Format(r)) : null,
        game is { } g ? new XElement("GameTime", Format(g)) : null,
    ];

    /// <summary>LiveSplit's time format: <c>hh:mm:ss.fffffff</c> (days first when there are any).</summary>
    public static string Format(TimeSpan time) => time.ToString("c", CultureInfo.InvariantCulture) is var text && text.Contains('.', StringComparison.Ordinal)
        ? text
        : text + ".0000000";

    private static TimeSpan? Time(XElement? element) =>
        element is not null && TimeSpan.TryParse(element.Value.Trim(), CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero ? time : null;

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;

    private static PlayableCharacter CharacterOf(string text) =>
        text.Contains("Jill", StringComparison.OrdinalIgnoreCase) ? PlayableCharacter.Jill
        : text.Contains("Chris", StringComparison.OrdinalIgnoreCase) ? PlayableCharacter.Chris
        : text.Contains("Rebecca", StringComparison.OrdinalIgnoreCase) ? PlayableCharacter.Rebecca
        : PlayableCharacter.Unknown;

    /// <summary>Names compared without case, spaces or punctuation ("002-Key" = "002 key").</summary>
    private static string Normalize(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Slug(string text) =>
        string.Concat(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
}
