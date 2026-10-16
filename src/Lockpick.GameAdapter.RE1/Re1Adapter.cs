using System.Reflection;
using Lockpick.Core.Adapters;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Memory;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Adapter for the original Resident Evil (1996) PC release.</summary>
public sealed class Re1Adapter : IGameAdapter
{
    public const string Id = "re1";

    /// <summary>GOG product id of "Resident Evil" (used to locate the install via the registry).</summary>
    public const string GogProductId = "1580232252";

    private const string ResourcePrefix = "Lockpick.GameAdapter.RE1.Profiles.";

    public Re1Adapter(IEnumerable<VersionProfile>? additionalProfiles = null)
    {
        var profiles = LoadEmbeddedProfiles().ToList();
        foreach (var extra in additionalProfiles ?? [])
        {
            if (extra.GameId != Id)
            {
                continue;
            }

            // A user-supplied profile with the same id overrides the built-in one.
            profiles.RemoveAll(p => p.Id == extra.Id);
            profiles.Add(extra);
        }

        Profiles = profiles;
    }

    public string GameId => Id;

    public string DisplayName => "Resident Evil";

    public IReadOnlyCollection<string> ExecutableNames { get; } = ["ResidentEvil.exe"];

    public IReadOnlyList<VersionProfile> Profiles { get; }

    public ProfileMatch Identify(ExecutableFingerprint fingerprint) => ProfileMatcher.Match(Profiles, fingerprint);

    public IGameSession Attach(IAttachedProcess process, VersionProfile profile)
    {
        var problems = ImageVerifier.Verify(process, profile);
        if (problems.Count > 0)
        {
            throw new AttachRejectedException($"Running image does not match profile {profile.Id}: {string.Join("; ", problems)}");
        }

        return new Re1Session(new ValidatedMemory(process, ImageVerifier.AllowedRanges(profile)), profile, ActorInfo);
    }

    public Core.Insight.IChangeExplainer CreateChangeExplainer(VersionProfile profile) =>
        new Re1ChangeExplainer(Re1Knowledge.LoadEmbedded());

    public Core.Trainer.IGameTrainer CreateTrainer(VersionProfile profile) => _trainer ??= new Re1Trainer(ItemName);

    private Re1Trainer? _trainer;

    public Core.Challenges.IChallengeRules CreateChallengeRules(VersionProfile profile) => (Re1Trainer)CreateTrainer(profile);

    /// <summary>The GOG game saves to SAVE\savedatN.dat next to the executable (one file per slot, 1–8; B112, #37).</summary>
    public string? SaveDirectory(string executablePath) =>
        Path.GetDirectoryName(executablePath) is { } folder ? Path.Combine(folder, "SAVE") : null;

    public int? SaveSlot(string fileName) =>
        fileName.Length == 12 && fileName.StartsWith("savedat", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && fileName[7] is >= '1' and <= '8'
            ? fileName[7] - '0'
            : null;

    public string WindowClass => "RESIDENT EVIL";

    /// <summary>
    /// GOG's DirectDraw wrapper settings (<c>dxcfg.ini</c>, docs/DISPLAY.md): <c>aspect=disabled</c> stretches the
    /// 640×480 picture over the window, <c>scaling=integer</c> uses whole multiples, otherwise it is fitted at 4:3.
    /// </summary>
    public Core.Display.GameDisplaySettings DisplaySettings(string installDirectory) =>
        Re1Display.Read(Path.Combine(installDirectory, "dxcfg.ini"));

    public Core.Insight.IProgressTracker CreateProgressTracker(VersionProfile profile) =>
        new Re1ProgressTracker(Re1Knowledge.LoadEmbedded());

    private static readonly Lazy<Re1Knowledge> Knowledge = new(Re1Knowledge.LoadEmbedded);

    // Bosses by enemy type (names.json): Black Tiger, Plant 42, Neptune, Tyrant, Yawn, final Tyrant, Yawn's second fight.
    private static readonly HashSet<int> Bosses = [4, 8, 11, 12, 13, 16, 18];

    internal static (string Name, Core.State.ActorKind Kind) ActorInfo(int type) =>
        (Knowledge.Value.Enemy(type), type >= 0x20 ? Core.State.ActorKind.Npc : Bosses.Contains(type) ? Core.State.ActorKind.Boss : Core.State.ActorKind.Enemy);

    public string? ItemName(int itemId) => Knowledge.Value.ItemNames.TryGetValue(itemId, out var name) ? name : null;

    public string? RoomName(string roomId) => Knowledge.Value.RoomNames.TryGetValue(roomId, out var name) ? name : null;

    public Core.Maps.IRoomMapSource CreateRoomMaps(string installDirectory)
    {
        var knowledge = Re1Knowledge.LoadEmbedded();
        return new Re1RoomMaps(installDirectory, knowledge.Item, knowledge.Fills);
    }

    public static IEnumerable<VersionProfile> LoadEmbeddedProfiles()
    {
        var assembly = typeof(Re1Adapter).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded profile {name}.");
            yield return ProfileLoader.Load(stream);
        }
    }
}
