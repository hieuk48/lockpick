using System.Globalization;
using System.Net;
using System.Text.Json;
using Lockpick.Core.Runs;

namespace Lockpick.App.Services;

/// <summary>The speedrun.com account Lockpick knows: from the API key, or just a name to find the player's runs.</summary>
public sealed record SpeedrunComUser(string Id, string Name);

/// <summary>
/// speedrun.com's public API (https://github.com/speedruncomorg/api, version 1): leaderboards (read-only, anonymous)
/// and the player's profile from their API key. Only ever talks to https://www.speedrun.com, follows no redirects
/// (a redirect elsewhere must never carry the key), names itself in the User-Agent as the API asks, and caches boards
/// for an hour (the API allows 100 requests a minute per IP). Data is CC BY-NC 4.0: the app credits speedrun.com.
/// </summary>
public sealed class SpeedrunCom : IDisposable
{
    public static readonly Uri Site = new("https://www.speedrun.com/");
    public const string KeyPage = "https://www.speedrun.com/api/auth";
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly Dictionary<string, LeaderboardResult> _boards = [];
    private readonly object _gate = new();

    public SpeedrunCom(string appVersion, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            BaseAddress = new Uri(Site, "api/v1/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lockpick/{appVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The board the Runs page shows now (for the overlay's world record), or null.</summary>
    public LeaderboardResult? Shown
    {
        get;
        set
        {
            field = value;
            ShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ShownChanged;

    /// <summary>The board for <paramref name="choices"/> (filter key → choice key), from the cache when it is under an hour old.</summary>
    public async Task<LeaderboardResult> LeaderboardAsync(LeaderboardDefinition board, IReadOnlyDictionary<string, string> choices,
        bool refresh = false, CancellationToken cancel = default)
    {
        var query = string.Join("&", board.Filters
            .Where(f => choices.TryGetValue(f.Key, out _))
            .Select(f => (f, choice: f.Choices.FirstOrDefault(c => c.Key == choices[f.Key])))
            .Where(x => x.choice is not null)
            .Select(x => $"var-{Uri.EscapeDataString(x.f.VariableId)}={Uri.EscapeDataString(x.choice!.Id)}"));
        var path = $"leaderboards/{Uri.EscapeDataString(board.Game)}/category/{Uri.EscapeDataString(board.Category)}?embed=players"
            + (query.Length > 0 ? "&" + query : string.Empty);
        lock (_gate)
        {
            if (!refresh && _boards.TryGetValue(path, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < CacheTime)
            {
                return cached;
            }
        }

        using var document = await GetAsync(path, key: null, cancel).ConfigureAwait(false);
        var result = ParseBoard(document.RootElement.GetProperty("data"), board.TimeField);
        lock (_gate)
        {
            _boards[path] = result;
        }

        return result;
    }

    /// <summary>
    /// The account an API key belongs to (GET /profile): checks a key before Lockpick keeps it. Throws
    /// <see cref="SpeedrunComException"/> when speedrun.com refuses the key.
    /// </summary>
    public async Task<SpeedrunComUser> ProfileAsync(string key, CancellationToken cancel = default)
    {
        using var document = await GetAsync("profile", key, cancel).ConfigureAwait(false);
        return User(document.RootElement.GetProperty("data"));
    }

    /// <summary>The account with this name, or null (GET /users?lookup=…, anonymous).</summary>
    public async Task<SpeedrunComUser?> FindUserAsync(string name, CancellationToken cancel = default)
    {
        using var document = await GetAsync($"users?lookup={Uri.EscapeDataString(name)}", key: null, cancel).ConfigureAwait(false);
        return document.RootElement.GetProperty("data").EnumerateArray().Select(User).FirstOrDefault();
    }

    private async Task<JsonDocument> GetAsync(string path, string? key, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
        {
            request.Headers.Add("X-API-Key", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SpeedrunComException($"speedrun.com didn't answer ({ex.Message}).", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden && key is not null)
            {
                throw new SpeedrunComException("speedrun.com didn't accept that key. Copy it again from your speedrun.com settings (API key).");
            }

            if ((int)response.StatusCode == 420)
            {
                throw new SpeedrunComException("speedrun.com asks to slow down (too many requests); try again in a minute.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SpeedrunComException($"speedrun.com answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                try
                {
                    return await JsonDocument.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new SpeedrunComException("speedrun.com sent something Lockpick can't read.", ex);
                }
            }
        }
    }

    internal static LeaderboardResult ParseBoard(JsonElement data, string timeField)
    {
        var names = new Dictionary<string, string>();
        if (data.TryGetProperty("players", out var players) && players.TryGetProperty("data", out var list))
        {
            foreach (var player in list.EnumerateArray())
            {
                if (player.TryGetProperty("id", out var id) && id.GetString() is { } playerId)
                {
                    names[playerId] = Name(player);
                }
            }
        }

        var entries = new List<LeaderboardEntry>();
        foreach (var item in data.GetProperty("runs").EnumerateArray())
        {
            var run = item.GetProperty("run");
            var times = run.GetProperty("times");
            var seconds = times.TryGetProperty($"{timeField}_t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble()
                : times.TryGetProperty("primary_t", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            var first = run.GetProperty("players").EnumerateArray().FirstOrDefault();
            string? runnerId = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var rid) ? rid.GetString() : null;
            var runner = runnerId is not null && names.TryGetValue(runnerId, out var known) ? known
                : first.ValueKind == JsonValueKind.Object && first.TryGetProperty("name", out var guest) ? guest.GetString() ?? "?" : "?";
            DateOnly? date = run.TryGetProperty("date", out var d) && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var day) ? day : null;
            entries.Add(new LeaderboardEntry(item.GetProperty("place").GetInt32(), runner, runnerId, TimeSpan.FromSeconds(seconds), date,
                run.TryGetProperty("weblink", out var link) ? link.GetString() : null));
        }

        return new LeaderboardResult(entries, data.TryGetProperty("weblink", out var web) ? web.GetString() : null, DateTimeOffset.UtcNow);
    }

    private static SpeedrunComUser User(JsonElement user) => new(user.GetProperty("id").GetString()!, Name(user));

    private static string Name(JsonElement user) =>
        user.TryGetProperty("names", out var names) && names.TryGetProperty("international", out var name) ? name.GetString() ?? "?"
        : user.TryGetProperty("name", out var guest) ? guest.GetString() ?? "?" : "?";
}

/// <summary>speedrun.com refused or didn't answer; the message is for the player.</summary>
public sealed class SpeedrunComException : Exception
{
    public SpeedrunComException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public SpeedrunComException()
    {
    }

    public SpeedrunComException(string message)
        : base(message)
    {
    }
}
