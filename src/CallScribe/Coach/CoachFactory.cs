using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Profiles;

namespace CallScribe.Coach;

/// <summary>Picks the advisor and (optionally) the memory store for a run. Everything
/// degrades gracefully: no Ollama → stub advisor; no Postgres → no memory. The coach
/// panel always works; it just does less without the local services.</summary>
public static class CoachFactory
{
    /// <summary>Returns (advisor, usingModel). usingModel is false when the stub was chosen,
    /// so callers can tell the user why advice looks canned.</summary>
    public static (IAdvisor Advisor, bool UsingModel) CreateAdvisor(
        AppConfig config, bool forceStub, IMemoryStore? memory = null, CoachingProfileStore? profiles = null)
    {
        if (forceStub) return (new StubAdvisor(), false);

        var chat = new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive);
        return chat.IsReachable()
            ? (new LlmAdvisor(chat, config.FastModel, memory, config.CoachRecallMaxDistance,
                   profiles, config.SelfSpeakerName), true)
            : (new StubAdvisor(), false);
    }

    /// <summary>The per-person coaching-profile store, or null when the feature is off. Construction is
    /// cheap (markdown files, no Ollama/Postgres dependency), so callers can build it unconditionally.</summary>
    public static CoachingProfileStore? CreateProfileStore(AppConfig config)
    {
        if (!config.CoachingProfilesEnabled) return null;
        var dir = string.IsNullOrWhiteSpace(config.CoachingProfilesDir)
            ? AppPaths.CoachingDir : config.CoachingProfilesDir;
        return new CoachingProfileStore(dir);
    }

    /// <summary>The end-of-meeting coaching-profile updater, or null when the feature is off. The
    /// memory store supplies the transcript; Ollama unreachability surfaces as a thrown call the
    /// best-effort caller already swallows, matching MeetingConsolidator.</summary>
    public static CoachingProfileUpdater? TryCreateProfileUpdater(AppConfig config, IMemoryStore memory)
    {
        var store = CreateProfileStore(config);
        if (store == null) return null;
        var chat = new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive);
        return new CoachingProfileUpdater(chat, config.ReasoningModel, memory, store, config.SelfSpeakerName);
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
