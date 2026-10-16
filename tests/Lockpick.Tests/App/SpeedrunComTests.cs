using System.Net;
using System.Text;
using Lockpick.App.Services;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.Tests.App;

public sealed class SpeedrunComTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-src-" + Guid.NewGuid().ToString("N"));
    private readonly StubHandler _handler = new();
    private readonly SpeedrunCom _client;

    public SpeedrunComTests() => _client = new SpeedrunCom("9.9.9", _handler);

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>A board shaped like speedrun.com's (GET /leaderboards/…?embed=players): times in the 'realtime' field.</summary>
    private const string Board = """
        {"data":{"weblink":"https://www.speedrun.com/residentevil#PC","timing":"realtime","runs":[
          {"place":1,"run":{"weblink":"https://www.speedrun.com/residentevil/run/a","date":"2025-11-02","players":[{"rel":"user","id":"u1"}],"times":{"primary_t":2281,"realtime_t":2281,"ingame_t":0}}},
          {"place":2,"run":{"weblink":"https://www.speedrun.com/residentevil/run/b","date":"2024-12-07","players":[{"rel":"user","id":"u2"}],"times":{"primary_t":2284.5,"realtime_t":2284.5,"ingame_t":0}}},
          {"place":3,"run":{"date":null,"players":[{"rel":"guest","name":"Guest Runner"}],"times":{"primary_t":2300,"realtime_t":2300,"ingame_t":0}}}],
          "players":{"data":[{"id":"u1","names":{"international":"Hamo"}},{"id":"u2","names":{"international":"LanceLM"}}]}}}
        """;

    private static readonly LeaderboardDefinition Re1Board = new Re1RunRules().Leaderboard;

    [Fact]
    public async Task A_board_is_read_with_names_places_and_the_board_s_time()
    {
        _handler.Respond(HttpStatusCode.OK, Board);
        var board = await _client.LeaderboardAsync(Re1Board, new Dictionary<string, string> { ["character"] = "jill", ["ending"] = "bad" });

        Assert.Equal(["Hamo", "LanceLM", "Guest Runner"], board.Entries.Select(e => e.Runner));
        Assert.Equal(TimeSpan.FromSeconds(2281), board.Record!.Time);
        Assert.Equal(new DateOnly(2025, 11, 2), board.Record.Date);
        Assert.Equal(2, board.PlaceFor(TimeSpan.FromSeconds(2282)));
        Assert.Equal(1, board.PlaceFor(TimeSpan.FromSeconds(2200)));
        Assert.Equal("LanceLM", board.Best("u2", null)!.Runner);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("www.speedrun.com", request.Uri.Host);
        Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
        Assert.Contains("var-p85077ng=21gny96l", request.Uri.Query, StringComparison.Ordinal); // Jill
        Assert.Contains("var-yn2koen5=5le2d2ml", request.Uri.Query, StringComparison.Ordinal); // Bad Ending
        Assert.Null(request.Key);                                                          // anonymous
        Assert.StartsWith("Lockpick/9.9.9", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Boards_are_cached_unless_refreshed()
    {
        _handler.Respond(HttpStatusCode.OK, Board);
        var choices = new Dictionary<string, string> { ["character"] = "chris" };
        await _client.LeaderboardAsync(Re1Board, choices);
        await _client.LeaderboardAsync(Re1Board, choices);
        Assert.Single(_handler.Requests);
        await _client.LeaderboardAsync(Re1Board, choices, refresh: true);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task A_key_is_checked_with_its_profile_and_a_refused_key_says_so()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"u9","names":{"international":"Runner"}}}""");
        var user = await _client.ProfileAsync("abcdef123456");
        Assert.Equal(new SpeedrunComUser("u9", "Runner"), user);
        Assert.Equal("abcdef123456", _handler.Requests[0].Key);
        Assert.EndsWith("/api/v1/profile", _handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);

        _handler.Respond(HttpStatusCode.Forbidden, """{"status":403}""");
        var refused = await Assert.ThrowsAsync<SpeedrunComException>(() => _client.ProfileAsync("wrongkey123"));
        Assert.Contains("didn't accept that key", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_key_never_goes_elsewhere()
    {
        _handler.Respond(HttpStatusCode.Redirect, string.Empty, location: "https://example.com/steal");
        await Assert.ThrowsAsync<SpeedrunComException>(() => _client.ProfileAsync("abcdef123456"));
        Assert.Single(_handler.Requests); // nothing sent to the redirect's target
    }

    [Fact]
    public async Task The_account_keeps_a_working_key_encrypted_and_refuses_junk_without_asking()
    {
        var settings = new SettingsStore(Path.Combine(_folder, "settings.json"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var secrets = new SecretStore(Path.Combine(_folder, "secrets"));
        var account = new SpeedrunAccount(_client, secrets, settings);

        await Assert.ThrowsAsync<SpeedrunComException>(() => account.SaveKeyAsync("not a key!"));
        Assert.Empty(_handler.Requests); // junk is refused before anything goes online

        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"u9","names":{"international":"Runner"}}}""");
        await account.SaveKeyAsync("  abcdef123456  ");
        Assert.True(account.HasKey);
        Assert.Equal("abcdef123456", account.Key());
        Assert.Equal(new SpeedrunComUser("u9", "Runner"), account.User);
        Assert.DoesNotContain("abcdef123456", File.ReadAllText(Path.Combine(_folder, "settings.json")), StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(_folder, "secrets")))
        {
            Assert.DoesNotContain("abcdef123456", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }

        account.RemoveKey();
        Assert.False(account.HasKey);
        Assert.Null(account.Key());
    }

    private static RunSubmission Submission() => new("mke5yl9k", "8gej2n93",
        new Dictionary<string, string> { ["p85077ng"] = "21gny96l", ["yn2koen5"] = "5le2d2ml" }, "realtime",
        TimeSpan.FromSeconds(2401.456), new DateOnly(2026, 10, 16), new Uri("https://youtu.be/abc"), "Timed by Lockpick");

    [Fact]
    public async Task A_run_is_submitted_as_the_api_documents_it()
    {
        _handler.Respond(HttpStatusCode.Created, """{"data":{"id":"r1","weblink":"https://www.speedrun.com/residentevil/run/r1"}}""");
        var run = await _client.SubmitAsync("abcdef123456", Submission());

        Assert.Equal(new SubmittedRun("r1", "https://www.speedrun.com/residentevil/run/r1"), run);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/api/v1/runs", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("abcdef123456", request.Key);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
        var sent = body.RootElement.GetProperty("run");
        Assert.Equal("mke5yl9k", sent.GetProperty("category").GetString());
        Assert.Equal("8gej2n93", sent.GetProperty("platform").GetString());
        Assert.Equal("2026-10-16", sent.GetProperty("date").GetString());
        Assert.False(sent.GetProperty("verified").GetBoolean()); // the moderators verify, never Lockpick
        Assert.Equal(2401.456, sent.GetProperty("times").GetProperty("realtime").GetDouble()); // RE1 keeps IGT in 'realtime'
        Assert.Equal("https://youtu.be/abc", sent.GetProperty("video").GetString());
        Assert.Equal("pre-defined", sent.GetProperty("variables").GetProperty("p85077ng").GetProperty("type").GetString());
        Assert.Equal("21gny96l", sent.GetProperty("variables").GetProperty("p85077ng").GetProperty("value").GetString());
        Assert.DoesNotContain("abcdef123456", request.Body!, StringComparison.Ordinal); // the key only ever in the header
    }

    [Fact]
    public async Task A_refused_run_says_why_in_speedrun_com_s_words()
    {
        _handler.Respond(HttpStatusCode.BadRequest, """{"status":400,"message":"no","errors":["[video] is missing and it is required"]}""");
        var refused = await Assert.ThrowsAsync<SpeedrunComException>(() => _client.SubmitAsync("abcdef123456", Submission()));
        Assert.Contains("[video] is missing", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_secret_store_reads_back_what_it_keeps_and_nothing_once_deleted()
    {
        var store = new SecretStore(Path.Combine(_folder, "s"));
        Assert.Null(store.Load("x"));
        store.Save("x", "s3cret-value");
        Assert.Equal("s3cret-value", store.Load("x"));
        File.WriteAllBytes(Directory.GetFiles(Path.Combine(_folder, "s"))[0], [1, 2, 3]);
        Assert.Null(store.Load("x")); // damaged: unreadable, not an error
        store.Delete("x");
        Assert.False(store.Exists("x"));
    }

    [Theory]
    [InlineData("re1-jill-bad-nmg-segments", "jill", "bad", "nmg", "none")]
    [InlineData("re1-chris-good-mg-segments", "chris", "best", "mg", "none")]
    [InlineData("re1-jill-nmg-key-items", "jill", "bad", "nmg", "none")] // Any%: the fastest (bad) ending
    public void Split_sets_pick_their_board(string id, string character, string ending, string glitches, string restriction)
    {
        var rules = new Re1RunRules();
        var board = rules.LeaderboardFor(rules.SplitSets.Single(s => s.Id == id));
        Assert.Equal((character, ending, glitches, restriction), (board["character"], board["ending"], board["glitches"], board["restriction"]));
    }

    [Fact]
    public void A_knife_only_file_is_put_on_the_knife_only_board()
    {
        var set = new SplitSet("lss-x", "Jill - Bad Ending - NMG - Knife Only", "Jill - Bad Ending - NMG - Knife Only", PlayableCharacter.Jill, [], false, "file");
        Assert.Equal("knife", new Re1RunRules().LeaderboardFor(set)["restriction"]);
    }

    private sealed record Seen(Uri Uri, string? Key, string UserAgent, string Method = "GET", string? Body = null);

    private sealed class StubHandler : HttpMessageHandler
    {
        private (HttpStatusCode Status, string Body, string? Location) _next = (HttpStatusCode.OK, "{}", null);

        public List<Seen> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body, string? location = null) => _next = (status, body, location);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.RequestUri!, request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.Single() : null,
                request.Headers.UserAgent.ToString(), request.Method.Method,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
            var response = new HttpResponseMessage(_next.Status) { Content = new StringContent(_next.Body, Encoding.UTF8, "application/json") };
            if (_next.Location is { } location)
            {
                response.Headers.Location = new Uri(location);
            }

            return Task.FromResult(response);
        }
    }
}
