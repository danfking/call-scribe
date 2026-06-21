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
    public SpeakerResolver(IVoiceprintStore? store, double enrolledMaxDistance = 0.30, double sessionMergeDistance = 0.55)
    {
        _store = store;
        _enrolledMaxDistance = enrolledMaxDistance;
        _sessionMergeDistance = sessionMergeDistance;
    }

    /// <summary>Resolve an embedding to a name, consulting enrolled voiceprints first.
    /// A store error degrades to session clustering rather than failing the caption.</summary>
    public async Task<string> ResolveAsync(float[] embedding, CancellationToken ct)
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

        return AssignSession(embedding);
    }

    /// <summary>Session-local online clustering with no store lookup. Public for direct
    /// testing and for the after-meeting pass, which clusters offline and names afterwards.</summary>
    public string AssignSession(float[] embedding)
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
