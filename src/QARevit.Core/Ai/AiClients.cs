using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QARevit.Core.Ai;

public enum AiProvider { Anthropic, OpenAiCompatible }

public sealed record ChatMessage(string Role, string Content);

/// <summary>Pengaturan AI. ApiKey tidak ikut disimpan di file JSON (disimpan terenkripsi oleh add-in).</summary>
public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.Anthropic;
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int MaxTokens { get; set; } = 2048;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
}

public interface IAiClient
{
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

public static class AiClientFactory
{
    public static IAiClient Create(AiSettings s, HttpClient? http = null) => s.Provider switch
    {
        AiProvider.Anthropic => new AnthropicClient(s, http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) }),
        _ => new OpenAiCompatibleClient(s, http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) }),
    };
}

/// <summary>Claude Messages API (POST /v1/messages).</summary>
public sealed class AnthropicClient : IAiClient
{
    private readonly AiSettings _s;
    private readonly HttpClient _http;

    public AnthropicClient(AiSettings s, HttpClient http) { _s = s; _http = http; }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["max_tokens"] = _s.MaxTokens,
            ["system"] = system,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _s.BaseUrl.TrimEnd('/') + "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _s.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var json = await AiHttp.SendAsync(_http, req, ct).ConfigureAwait(false);
        var content = json["content"]?.AsArray() ?? new JsonArray();
        return string.Concat(content.Where(c => c?["type"]?.GetValue<string>() == "text")
            .Select(c => c!["text"]!.GetValue<string>()));
    }
}

/// <summary>Endpoint kompatibel OpenAI (POST {baseUrl}/chat/completions) untuk API key/penyedia custom.</summary>
public sealed class OpenAiCompatibleClient : IAiClient
{
    private readonly AiSettings _s;
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(AiSettings s, HttpClient http) { _s = s; _http = http; }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var all = new List<JsonNode> { new JsonObject { ["role"] = "system", ["content"] = system } };
        all.AddRange(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }));
        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["max_tokens"] = _s.MaxTokens,
            ["messages"] = new JsonArray(all.ToArray()),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _s.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.ApiKey);

        var json = await AiHttp.SendAsync(_http, req, ct).ConfigureAwait(false);
        return json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }
}

internal static class AiHttp
{
    public static async Task<JsonNode> SendAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var detail = text;
            try { detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; }
            catch (JsonException) { }
            throw new HttpRequestException($"AI API {(int)res.StatusCode}: {detail}");
        }
        return JsonNode.Parse(text) ?? throw new HttpRequestException("Respon AI kosong.");
    }
}
