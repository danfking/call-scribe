using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CallScribe.Coach.Llm;

/// <summary>Talks to a local Ollama server over its HTTP API (no extra NuGet, no data
/// leaving the machine). Non-streaming single-shot completions; thinking models like
/// qwen3 are asked not to think (and any stray &lt;think&gt; block is stripped) so the
/// reply is the advice, not its reasoning.</summary>
public sealed partial class OllamaChat : ICoachChat
{
    // No proxy: the server is on localhost and a system proxy can both slow the first
    // request (handler warmup/auto-detect) and wrongly route loopback traffic.
    private static readonly HttpClient Http =
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _baseUrl;
    private readonly string _keepAlive;

    public OllamaChat(string baseUrl, string keepAlive = "10m")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _keepAlive = keepAlive;
    }

    /// <summary>Quick liveness probe so callers can fall back to the stub advisor when
    /// Ollama isn't running, instead of failing on every utterance.</summary>
    public bool IsReachable()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = Http.GetAsync($"{_baseUrl}/api/version", cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CompleteAsync(
        string model, string system, string user, bool jsonMode, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Think = false,
            KeepAlive = _keepAlive,
            Format = jsonMode ? "json" : null,
            Options = new ChatOptions { Temperature = 0.2, NumPredict = 300 },
            Messages =
            [
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = user },
            ],
        };

        using var resp = await Http.PostAsJsonAsync($"{_baseUrl}/api/chat", request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(ct).ConfigureAwait(false);
        var content = body?.Message?.Content ?? "";
        return StripThink(content).Trim();
    }

    private static string StripThink(string text) => ThinkBlock().Replace(text, "").Trim();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("think")] public bool Think { get; init; }
        [JsonPropertyName("keep_alive")] public string? KeepAlive { get; init; }
        [JsonPropertyName("format")] public string? Format { get; init; }
        [JsonPropertyName("options")] public ChatOptions? Options { get; init; }
        [JsonPropertyName("messages")] public required List<ChatMessage> Messages { get; init; }
    }

    private sealed class ChatOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
    }
}
