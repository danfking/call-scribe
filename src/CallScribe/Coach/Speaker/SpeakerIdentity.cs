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
    private readonly string? _selfName;
    private readonly double _selfThreshold;

    private SpeakerIdentity(
        ISpeakerEmbedder embedder, SpeakerResolver resolver, IVoiceprintStore? voiceprints,
        string? selfName, double selfThreshold)
    {
        _embedder = embedder;
        _resolver = resolver;
        _voiceprints = voiceprints;
        _selfName = string.IsNullOrWhiteSpace(selfName) ? null : selfName;
        _selfThreshold = selfThreshold;
    }

    /// <summary>Build the live service from config, or null if unavailable.</summary>
    public static async Task<SpeakerIdentity?> TryCreateAsync(AppConfig config, CancellationToken ct)
    {
        if (!config.SpeakerIdEnabled) return null;

        var embedder = TryCreateEmbedder(config);
        if (embedder == null) return null;

        var voiceprints = await TryCreateVoiceprintsAsync(config, embedder.Dimensions, ct).ConfigureAwait(false);
        var resolver = new SpeakerResolver(
            voiceprints, config.VoiceprintMaxDistance, config.SessionMergeDistance, config.LiveMinSpeakerSeconds);
        return new SpeakerIdentity(embedder, resolver, voiceprints, config.SelfSpeakerName, config.SelfMatchMaxDistance);
    }

    /// <summary>Resolve a far-side caption's 16 kHz mono samples to a speaker name, falling
    /// back to the generic "Others" label when the audio is too short to characterise.</summary>
    public async Task<string> ResolveAsync(float[] samples16kMono, CancellationToken ct)
    {
        var embedding = _embedder.Embed(samples16kMono);
        if (embedding.Length == 0) return LiveCaptionEngine.OthersLabel;
        var clipSeconds = samples16kMono.Length / (double)SpeakerAudio.SampleRate;
        return await _resolver.ResolveAsync(embedding, clipSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>Apply a live name assignment: rename the session speaker so future captions use
    /// the name, and (if the voiceprint store is available) enroll its voiceprint so they are
    /// recognised next meeting. Returns false when no current speaker carries
    /// <paramref name="currentLabel"/> (e.g. a stale or mistyped label).</summary>
    public async Task<bool> AssignNameAsync(string currentLabel, string newName, CancellationToken ct)
    {
        var centroid = _resolver.Rename(currentLabel, newName);
        if (centroid == null) return false;

        if (_voiceprints != null)
        {
            try
            {
                // If the current label was an enrolled person, move their stored voiceprint to
                // the new name; otherwise enroll the new name from the live session centroid.
                var moved = await _voiceprints.RenameAsync(currentLabel, newName, ct).ConfigureAwait(false);
                if (!moved) await _voiceprints.EnrollAsync(newName, centroid, ct).ConfigureAwait(false);
            }
            catch { /* the live rename still applies even if persistence fails */ }
        }
        return true;
    }

    /// <summary>Decide whether a mic ("Me") caption is the user's own voice. Returns a
    /// no-opinion result (keep as "Me") when self isn't enrolled or the clip is too short to
    /// embed; otherwise keeps it (labelled with the user's name) when it matches the self
    /// voiceprint, or flags it as bleed to suppress when it clearly doesn't.</summary>
    public async Task<MeSpeakerResult> VerifyMeAsync(float[] samples16kMono, CancellationToken ct)
    {
        if (_selfName == null || _voiceprints == null) return new MeSpeakerResult(false, null);

        var embedding = _embedder.Embed(samples16kMono);
        if (embedding.Length == 0) return new MeSpeakerResult(false, null); // too short: keep (conservative)

        double? distance;
        try
        {
            distance = await _voiceprints.DistanceToAsync(_selfName, embedding, ct).ConfigureAwait(false);
        }
        catch
        {
            return new MeSpeakerResult(false, null); // store hiccup: never drop the user's speech
        }

        return DecideMe(distance, _selfThreshold, _selfName);
    }

    /// <summary>Pure self-verification policy. Conservative: only suppress when we have a
    /// real distance that exceeds the threshold; a null distance (self not enrolled, or no
    /// embedding) means "no opinion — keep as Me".</summary>
    public static MeSpeakerResult DecideMe(double? distanceToSelf, double threshold, string selfName)
    {
        if (distanceToSelf is not double d) return new MeSpeakerResult(false, null);
        return d <= threshold
            ? new MeSpeakerResult(false, selfName)   // it's you → keep, labelled
            : new MeSpeakerResult(true, null);        // not you → far-side bleed → suppress
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
        try { return new SherpaDiarizer(seg, emb, clusterThreshold: config.DiarizationClusterThreshold); }
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
