namespace CallScribe.Coach.Speaker;

/// <summary>Turns a far-side voice embedding into a name. Two stages:
///
/// <para>1. <b>Enrolled match.</b> Ask the voiceprint store for the nearest known person;
/// if it is within <c>enrolledMaxDistance</c>, use that real name.</para>
///
/// <para>2. <b>Session clustering.</b> Otherwise group the voice against the anonymous
/// speakers already seen this meeting: if it is within <c>sessionMergeDistance</c> of an
/// existing cluster it joins it (and refines that cluster's centroid), else it becomes a
/// new "Speaker N". This is online, single-pass clustering — good enough for live labels;
/// the after-meeting offline diarization pass is the authoritative cleanup.</para>
///
/// Only far-side ("Others") audio is resolved; the user's own mic stays "Me". The class is
/// thread-safe so it can be called from caption worker threads.</summary>
public sealed class SpeakerResolver
{
    private sealed class SessionSpeaker(string label, float[] centroid)
    {
        public string Label { get; set; } = label;
        public float[] Centroid { get; set; } = centroid;
        public int Count { get; set; } = 1;
    }

    private readonly IVoiceprintStore? _store;
    private readonly double _enrolledMaxDistance;
    private readonly double _sessionMergeDistance;
    private readonly double _minSpeakerSeconds;
    private readonly List<SessionSpeaker> _session = [];
    private readonly object _lock = new();
    private int _nextSpeaker = 1;

    /// <param name="store">Enrolled voiceprints, or null to skip enrolled matching (pure
    /// session clustering — every meeting starts anonymous).</param>
    /// <param name="enrolledMaxDistance">Max cosine distance to accept an enrolled match.</param>
    /// <param name="sessionMergeDistance">Max cosine distance to merge into an existing
    /// session cluster instead of starting a new "Speaker N". Looser than the enrolled
    /// threshold because clustering one meeting's voices is more forgiving than asserting
    /// a specific identity.</param>
    /// <param name="minSpeakerSeconds">A clip shorter than this is too brief to embed reliably,
    /// so it attaches to the nearest existing speaker rather than minting a new one. 0 disables
    /// the gate (e.g. the offline pass, which feeds already-substantial cluster means).</param>
    public SpeakerResolver(
        IVoiceprintStore? store, double enrolledMaxDistance = 0.30,
        double sessionMergeDistance = 0.55, double minSpeakerSeconds = 0)
    {
        _store = store;
        _enrolledMaxDistance = enrolledMaxDistance;
        _sessionMergeDistance = sessionMergeDistance;
        _minSpeakerSeconds = minSpeakerSeconds;
    }

    /// <summary>Resolve an embedding to a name, consulting enrolled voiceprints first.
    /// A store error degrades to session clustering rather than failing the caption.
    /// <paramref name="clipSeconds"/> gates minting a new session speaker from a too-short clip.</summary>
    public async Task<string> ResolveAsync(float[] embedding, double clipSeconds, CancellationToken ct)
    {
        if (embedding.Length == 0) return UnknownSpeaker();

        if (_store != null)
        {
            try
            {
                var match = await _store.IdentifyAsync(embedding, ct).ConfigureAwait(false);
                // Register the enrolled match as a session speaker too, so its live label is
                // consistent and can be renamed like any other (e.g. /rename "Joe" "Bob").
                if (match is { } hit && hit.Distance <= _enrolledMaxDistance) return TrackSession(hit.PersonName, embedding);
            }
            catch { /* fall through to session clustering */ }
        }

        return AssignSession(embedding, clipSeconds);
    }

    /// <summary>Session-local online clustering with no store lookup. Public for direct
    /// testing and for the after-meeting pass, which clusters offline and names afterwards.
    /// <paramref name="clipSeconds"/> defaults to "long enough" so callers that feed substantial
    /// audio (the offline pass, tests) always cluster normally.</summary>
    public string AssignSession(float[] embedding, double clipSeconds = double.MaxValue)
    {
        if (embedding.Length == 0) return UnknownSpeaker();

        lock (_lock)
        {
            SessionSpeaker? best = null;
            var bestDistance = double.MaxValue;
            foreach (var speaker in _session)
            {
                var d = VectorMath.CosineDistance(speaker.Centroid, embedding);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = speaker;
                }
            }

            if (best != null && bestDistance <= _sessionMergeDistance)
            {
                best.Centroid = VectorMath.RunningMean(best.Centroid, best.Count, embedding);
                best.Count++;
                return best.Label;
            }

            // Too short to trust as a distinct identity: attach to the nearest existing speaker
            // (without polluting its centroid) rather than mint a fragment speaker. With no
            // speakers yet, stay generic instead of seeding a cluster from an unreliable clip.
            if (clipSeconds < _minSpeakerSeconds) return best?.Label ?? UnknownSpeaker();

            var label = $"Speaker {_nextSpeaker++}";
            _session.Add(new SessionSpeaker(label, embedding));
            return label;
        }
    }

    /// <summary>Register or update a session speaker under a known name (an enrolled match) so
    /// its live label is renameable like any session speaker. Returns the label.</summary>
    private string TrackSession(string label, float[] embedding)
    {
        lock (_lock)
        {
            var speaker = _session.FirstOrDefault(s => s.Label == label);
            if (speaker != null)
            {
                speaker.Centroid = VectorMath.RunningMean(speaker.Centroid, speaker.Count, embedding);
                speaker.Count++;
            }
            else
            {
                _session.Add(new SessionSpeaker(label, embedding));
            }
            return label;
        }
    }

    /// <summary>Post-hoc consolidation of the session's speakers, run once the meeting's audio is
    /// in and the centroids have stabilised. The online pass mints a new "Speaker N" whenever a
    /// noisy clip lands beyond <c>sessionMergeDistance</c> of every existing centroid and can never
    /// undo it, so one person fragments into several labels — mostly brief, low-support fragments
    /// (a quick "yeah", a clipped word) that the single-pass clustering couldn't place.
    ///
    /// <para>This mirrors the offline <c>MergeSmallClusters</c>: a cluster with at least
    /// <paramref name="minSupport"/> clips is treated as a real speaker and protected, and each
    /// fragment below that is folded into its nearest substantial cluster when within
    /// <paramref name="mergeDistance"/>. Deliberately it never merges two substantial clusters with
    /// each other: on noisy live embeddings two real speakers can sit closer than a person's own
    /// fragments, so a blanket pairwise merge collapses distinct people (measured: attribution falls
    /// from ~96% to ~60%). Folding only the fragments keeps each real speaker intact.</para>
    ///
    /// Returns a map from each folded-away label to its surviving label so the caller can rewrite
    /// the already-emitted transcript.</summary>
    public IReadOnlyDictionary<string, string> Consolidate(double mergeDistance, int minSupport = 2)
    {
        lock (_lock)
        {
            var remap = new Dictionary<string, string>();
            var substantial = _session.Where(s => s.Count >= minSupport).ToList();
            if (substantial.Count == 0) return remap; // nothing solid to fold fragments into

            foreach (var fragment in _session.Where(s => s.Count < minSupport).ToList())
            {
                SessionSpeaker? nearest = null;
                var bestDistance = double.MaxValue;
                foreach (var speaker in substantial)
                {
                    var d = VectorMath.CosineDistance(fragment.Centroid, speaker.Centroid);
                    if (d < bestDistance) { bestDistance = d; nearest = speaker; }
                }
                if (nearest == null || bestDistance > mergeDistance) continue; // too far from any real speaker: keep it

                nearest.Centroid = WeightedMean(nearest.Centroid, nearest.Count, fragment.Centroid, fragment.Count);
                nearest.Count += fragment.Count;
                _session.Remove(fragment);
                remap[fragment.Label] = nearest.Label;
            }
            return remap;
        }
    }

    private static float[] WeightedMean(float[] c1, int n1, float[] c2, int n2)
    {
        var result = new float[c1.Length];
        for (var i = 0; i < result.Length; i++) result[i] = (c1[i] * n1 + c2[i] * n2) / (n1 + n2);
        return result;
    }

    /// <summary>Rename a session speaker (e.g. from a live /assign-name) so future captions in
    /// that voice use <paramref name="newLabel"/>, and return its averaged voiceprint for
    /// enrollment. Null when no current session speaker carries <paramref name="oldLabel"/>.</summary>
    public float[]? Rename(string oldLabel, string newLabel)
    {
        lock (_lock)
        {
            var speaker = _session.FirstOrDefault(s => s.Label == oldLabel);
            if (speaker == null) return null;
            speaker.Label = newLabel;
            return speaker.Centroid;
        }
    }

    private string UnknownSpeaker()
    {
        // Too little/too quiet audio to characterise: keep the generic far-side label
        // rather than minting a spurious new speaker.
        return Transcription.LiveCaptionEngine.OthersLabel;
    }
}
