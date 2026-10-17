using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.Core;

public class ProfileTests
{
    private static VersionProfile Re1 => Re1Adapter.LoadEmbeddedProfiles().Single(p => p.Id == "re1-gog-usa-hotfix4");

    private const string MinimalProfile = """
        {
          "id": "test", "gameId": "t", "displayName": "Test",
          "executable": { "fileName": "t.exe", "fileSize": 1, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                          "machine": "x86", "timeDateStamp": "0x1", "imageBase": "0x400000", "sizeOfImage": "0x1000" },
          "fields": [ FIELDS ]
        }
        """;

    [Fact]
    public void Embedded_re1_profile_is_valid_and_has_evidence_for_trusted_fields()
    {
        var profile = Re1;
        Assert.Equal("re1-gog-usa-hotfix4", profile.Id);
        Assert.Equal("205BD6FED6E080A38823958E5C3306CACC86A18FF9A2758179E8D33CAEEE4A78", profile.Executable.Sha256);
        Assert.Equal(0x400000ul, profile.Executable.ImageBase);
        Assert.Equal(0x49B2000u, profile.Executable.SizeOfImage);
        Assert.NotEmpty(profile.Anchors);
        Assert.All(profile.Fields.Where(f => f.IsTrusted), f => Assert.False(string.IsNullOrWhiteSpace(f.Evidence)));
        Assert.Equal(0x3u, profile.FindField("character")!.Mask);
    }

    [Fact]
    public void Field_outside_image_is_rejected()
    {
        var json = MinimalProfile.Replace("FIELDS", """{ "name": "x", "address": "0x500000", "type": "u8", "verification": "unverified" }""", StringComparison.Ordinal);
        var ex = Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(json));
        Assert.Contains("outside the image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_field_without_evidence_is_rejected()
    {
        var json = MinimalProfile.Replace("FIELDS", """{ "name": "x", "address": "0x400010", "type": "u8", "verification": "static" }""", StringComparison.Ordinal);
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(json));
    }

    [Fact]
    public void Duplicate_fields_and_bad_hashes_are_rejected()
    {
        var dup = MinimalProfile.Replace("FIELDS", """{ "name": "x", "address": "0x400010", "type": "u8" }, { "name": "X", "address": "0x400011", "type": "u8" }""", StringComparison.Ordinal);
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(dup));
        var badHash = MinimalProfile.Replace("FIELDS", string.Empty, StringComparison.Ordinal).Replace(new string('0', 64), "xyz", StringComparison.Ordinal);
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(badHash));
    }

    [Fact]
    public void Named_addresses_must_lie_in_the_image_and_trusted_ones_need_evidence()
    {
        static string With(string address) => MinimalProfile.Replace("\"fields\": [ FIELDS ]", $"\"fields\": [], \"addresses\": [ {address} ]", StringComparison.Ordinal);
        var outside = With("""{ "name": "table", "address": "0x400F00", "length": "0x200", "verification": "unverified" }""");
        Assert.Contains("outside the image", Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(outside)).Message, StringComparison.Ordinal);
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse(With("""{ "name": "table", "address": "0x400010", "verification": "static" }""")));

        var lead = ProfileLoader.Parse(With("""{ "name": "table", "address": "0x400010", "length": "0x10", "verification": "unverified" }"""));
        Assert.Null(lead.TrustedAddress("table")); // a lead is never used
        var known = ProfileLoader.Parse(With("""{ "name": "table", "address": "0x400010", "verification": "static", "evidence": "code" }"""));
        Assert.Equal(0x400010ul, known.TrustedAddress("TABLE"));
    }

    [Fact]
    public void Malformed_json_is_a_validation_error()
    {
        Assert.Throws<ProfileValidationException>(() => ProfileLoader.Parse("{ not json"));
    }

    [Fact]
    public void Exact_hash_and_size_match_is_supported()
    {
        var match = ProfileMatcher.Match([Re1], FakeFingerprintProvider.For(Re1, @"C:\Games\ResidentEvil.exe"));
        Assert.True(match.IsSupported);
        Assert.Equal(Re1.Id, match.Profile!.Id);
    }

    [Fact]
    public void Same_timestamp_different_hash_is_reported_as_modified()
    {
        var fp = FakeFingerprintProvider.For(Re1, "x.exe") with { Sha256 = new string('F', 64) };
        var match = ProfileMatcher.Match([Re1], fp);
        Assert.False(match.IsSupported);
        Assert.Contains("modified", match.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_and_64_bit_builds_are_unsupported()
    {
        var unknown = new ExecutableFingerprint("x.exe", 5, new string('B', 64), null);
        Assert.False(ProfileMatcher.Match([Re1], unknown).IsSupported);
        var x64 = unknown with { Pe = new PeImageInfo(PeMachine.X64, 1, 0x140000000, 0x1000, 0, 0x40) };
        Assert.Contains("architecture", ProfileMatcher.Match([Re1], x64).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_verifier_accepts_matching_image()
    {
        var process = new FakeAttachedProcess(1, new FakeMemory().WithVerifiedImage(Re1), new ModuleImage(0x400000, 0x49B2000, null));
        Assert.Empty(ImageVerifier.Verify(process, Re1));
    }

    [Fact]
    public void Image_verifier_reports_anchor_mismatch_and_relocation()
    {
        var memory = new FakeMemory().WithVerifiedImage(Re1);
        var anchor = Re1.Anchors[0];
        memory.WriteU8(anchor.Address, 0x90);
        var moved = new FakeAttachedProcess(1, memory, new ModuleImage(0x500000, 0x49B2000, null));

        var problems = ImageVerifier.Verify(moved, Re1);
        Assert.Contains(problems, p => p.Contains("loaded at 0x500000", StringComparison.Ordinal));
        var relocatedHeaderUnreadable = problems.Any(p => p.Contains("PE header", StringComparison.Ordinal));
        Assert.True(relocatedHeaderUnreadable);

        var sameBase = new FakeAttachedProcess(1, memory, new ModuleImage(0x400000, 0x49B2000, null));
        Assert.Contains(ImageVerifier.Verify(sameBase, Re1), p => p.Contains(anchor.Name, StringComparison.Ordinal));
    }
}
