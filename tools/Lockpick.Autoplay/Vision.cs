using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lockpick.Autoplay;

/// <summary>
/// Optional: asks a vision model on an OpenAI-compatible server what a screenshot shows (menus, cutscenes — screens
/// memory can't name). Set LOCKPICK_VISION_URL (e.g. http://host:8000/v1) and LOCKPICK_VISION_MODEL. Game state is
/// always checked from memory; the model only labels screens.
/// </summary>
internal static class Vision
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static bool IsConfigured => Environment.GetEnvironmentVariable("LOCKPICK_VISION_URL") is { Length: > 0 };

    public static async Task<string> AskAsync(string imagePath, string question)
    {
        var url = Environment.GetEnvironmentVariable("LOCKPICK_VISION_URL")!.TrimEnd('/') + "/chat/completions";
        var model = Environment.GetEnvironmentVariable("LOCKPICK_VISION_MODEL") ?? "default";
        var image = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath));
        var request = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 300,
            ["temperature"] = 0,
            // Qwen-style servers: answer directly instead of reasoning at length.
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } },
                    new JsonObject { ["type"] = "text", ["text"] = "This is a screenshot of Resident Evil (1996, PC). If it is entirely black, answer just \"black\". " + question }),
            }),
        };
        using var response = await Http.PostAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? string.Empty;
    }
}
