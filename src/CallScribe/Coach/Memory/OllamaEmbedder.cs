using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CallScribe.Coach.Memory;

/// <summary>Embeds text with a local Ollama embedding model (e.g. nomic-embed-text,
/// 768-dim). Same privacy story as the chat client: nothing leaves the machine.</summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private static readonly HttpClient Http =
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _keepAlive;

    public int Dimensions { get; }

    public OllamaEmbedder(string baseUrl, string model, int dimensions = 768, string keepAlive = "10m")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        Dimensions = dimensions;
        _keepAlive = keepAlive;
    }

    public async Task<float[]> EmbedAsync(string text, EmbedPurpose purpose, CancellationToken ct)
    {
        var request = new { model = _model, input = Prefix(purpose) + text, keep_alive = _keepAlive };
        using var resp = await Http.PostAsJsonAsync($"{_baseUrl}/api/embed", request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EmbedResponse>(ct).ConfigureAwait(false);
        var vector = body?.Embeddings?.FirstOrDefault()
                     ?? throw new InvalidOperationException("Ollama returned no embedding.");
        if (vector.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Embedding model '{_model}' returned {vector.Length} dims, expected {Dimensions}.");
        }
        return vector;
    }

    /// <summary>nomic-embed-text needs "search_document:" / "search_query:" task prefixes;
    /// without them retrieval quality collapses. Other models get no prefix.</summary>
    private string Prefix(EmbedPurpose purpose)
    {
        if (!_model.Contains("nomic", StringComparison.OrdinalIgnoreCase)) return "";
        return purpose == EmbedPurpose.Query ? "search_query: " : "search_document: ";
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")] public float[][]? Embeddings { get; init; }
    }
}
