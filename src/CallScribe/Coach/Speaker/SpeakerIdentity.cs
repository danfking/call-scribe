using CallScribe.Transcription;

namespace CallScribe.Coach.Speaker;

/// <summary>The live speaker-identification service: an embedder, the enrolled voiceprint
/// store, and a session resolver, assembled and ready to turn far-side audio into a name.
/// Everything is optional and degrades gracefully — <see cref="TryCreateAsync"/> returns
/// null when speaker id is off, the models are missing, or the native runtime can't load,
/// so callers simply fall back to the plain "Others" label. Also the home for the helpers
/// that build the diarizer and voiceprint store, so model/runtime probing lives in one place.</summary>
public sealed class SpeakerIdentity : IAsyncDisposable
{
    private readonly ISpeakerEmbedder _embedder;
    private readonly SpeakerResolver _resolver;
    private readonly IVoiceprintStore? _voiceprints;

    private SpeakerIdentity(ISpeakerEmbedder embedder, SpeakerResolver resolver, IVoiceprintStore? voiceprints)
    {
        _embedder = embedder;
        _resolver = resolver;
        _voiceprints = voiceprints;
    }

    /// <summary>Build the live service from config, or null if unavailable.</summary>
    public static async Task<SpeakerIdentity?> TryCreateAsync(AppConfig config, CancellationToken ct)
    {
        if (!config.SpeakerIdEnabled) return null;

        var embedder = TryCreateEmbedder(config);
        if (embedder == null) return null;

        var voiceprints = await TryCreateVoiceprintsAsync(config, embedder.Dimensions, ct).ConfigureAwait(false);
        var resolver = new SpeakerResolver(voiceprints, config.VoiceprintMaxDistance);
        return new SpeakerIdentity(embedder, resolver, voiceprints);
    }

    /// <summary>Resolve a far-side caption's 16 kHz mono samples to a speaker name, falling
    /// back to the generic "Others" label when the audio is too short to characterise.</summary>
    public async Task<string> ResolveAsync(float[] samples16kMono, CancellationToken ct)
    {
        var embedding = _embedder.Embed(samples16kMono);
        if (embedding.Length == 0) return LiveCaptionEngine.OthersLabel;
        return await _resolver.ResolveAsync(embedding, ct).ConfigureAwait(false);
    }

    // --- shared creation helpers (also used by enrollment and offline diarization) -----

    /// <summary>Create the embedder, or null if speaker id is off, the model file is absent,
    /// or the native runtime fails to load.</summary>
    public static ISpeakerEmbedder? TryCreateEmbedder(AppConfig config)
    {
        var path = ModelPath(config.SpeakerEmbedModel);
        if (path == null) return null;
        try { return new SherpaSpeakerEmbedder(path); }
        catch { return null; }
    }

    /// <summary>Create the offline diarizer, or null if either model is absent or the native
    /// runtime fails to load.</summary>
    public static SherpaDiarizer? TryCreateDiarizer(AppConfig config)
    {
        var seg = ModelPath(config.SpeakerSegModel);
        var emb = ModelPath(config.SpeakerEmbedModel);
        if (seg == null || emb == null) return null;
        try { return new SherpaDiarizer(seg, emb); }
        catch { return null; }
    }

    /// <summary>Create and initialise the voiceprint store, or null if Postgres is unreachable.</summary>
    public static async Task<VoiceprintStore?> TryCreateVoiceprintsAsync(AppConfig config, int dimensions, CancellationToken ct)
    {
        try
        {
            var store = new VoiceprintStore(config.PostgresConn, dimensions);
            await store.EnsureSchemaAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Absolute path to a model file in the models directory, or null if it is not
    /// present (so the caller can degrade rather than throw on a missing download).</summary>
    public static string? ModelPath(string fileName)
    {
        var path = Path.Combine(AppPaths.ModelsDir, fileName);
        return File.Exists(path) ? path : null;
    }

    public async ValueTask DisposeAsync()
    {
        _embedder.Dispose();
        if (_voiceprints != null) await _voiceprints.DisposeAsync().ConfigureAwait(false);
    }
}
