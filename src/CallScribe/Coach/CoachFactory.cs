using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;

namespace CallScribe.Coach;

/// <summary>Picks the advisor and (optionally) the memory store for a run. Everything
/// degrades gracefully: no Ollama → stub advisor; no Postgres → no memory. The coach
/// panel always works; it just does less without the local services.</summary>
public static class CoachFactory
{
    /// <summary>Returns (advisor, usingModel). usingModel is false when the stub was chosen,
    /// so callers can tell the user why advice looks canned.</summary>
    public static (IAdvisor Advisor, bool UsingModel) CreateAdvisor(
        AppConfig config, bool forceStub, IMemoryStore? memory = null)
    {
        if (forceStub) return (new StubAdvisor(), false);

        var chat = new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive);
        return chat.IsReachable()
            ? (new LlmAdvisor(chat, config.FastModel, memory, config.CoachRecallMaxDistance), true)
            : (new StubAdvisor(), false);
    }

    /// <summary>Build and initialise the memory store, or null if Postgres/Ollama aren't
    /// available. Schema creation doubles as the reachability check.</summary>
    public static async Task<MemoryStore?> TryCreateMemoryAsync(AppConfig config, CancellationToken ct)
    {
        try
        {
            var embedder = new OllamaEmbedder(config.OllamaUrl, config.EmbedModel, keepAlive: config.OllamaKeepAlive);
            var store = new MemoryStore(config.PostgresConn, embedder);
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch
        {
            return null;
        }
    }
}
