using CallScribe.Transcription;

namespace CallScribe.Coach.Speaker;

/// <summary>One diarized speaker on the far side, with the name it resolved to and the
/// averaged voiceprint of its turns (kept so an unknown speaker can be enrolled once named).</summary>
public sealed record SpeakerCluster(int Index, string Name, bool Enrolled, float[] MeanEmbedding);

/// <summary>Result of attributing the Others track: the raw diarized turns, the resolved
/// clusters, and a mapper from a transcript segment to the speaker name covering it.</summary>
public sealed class DiarizationResult
{
    private readonly IReadOnlyList<DiarizedSegment> _segments;
    private readonly Dictionary<int, string> _names;

    public IReadOnlyList<SpeakerCluster> Clusters { get; }

    public DiarizationResult(IReadOnlyList<DiarizedSegment> segments, IReadOnlyList<SpeakerCluster> clusters)
    {
        _segments = segments;
        Clusters = clusters;
        _names = clusters.ToDictionary(c => c.Index, c => c.Name);
    }

    /// <summary>Rename a cluster (e.g. after the user names and enrolls an unknown speaker)
    /// so <see cref="SpeakerFor"/> reflects the real name in the rewritten transcript.</summary>
    public void Rename(int clusterIndex, string name) => _names[clusterIndex] = name;

    /// <summary>The speaker name covering a transcript segment, by greatest time overlap with
    /// a diarized turn; falls back to the generic label when nothing overlaps.</summary>
    public string SpeakerFor(TranscriptSegment segment)
    {
        var bestOverlap = 0.0;
        int? bestCluster = null;
        foreach (var turn in _segments)
        {
            var overlap = Math.Min(segment.End, turn.End) - Math.Max(segment.Start, turn.Start);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestCluster = turn.Speaker;
            }
        }
        return bestCluster is { } c && _names.TryGetValue(c, out var name) ? name : LiveCaptionEngine.OthersLabel;
    }
}

/// <summary>Runs the authoritative after-meeting pass: offline-diarize the Others track,
/// average each speaker's voiceprint, and resolve it against the enrolled voiceprints (or to
/// an anonymous "Speaker N"). Offline clustering sees the whole recording, so it is far more
/// accurate than the live single-pass guesser, and it produces the embeddings used to enroll
/// newly-named speakers.</summary>
public static class OfflineDiarization
{
    /// <param name="embedder">Embedder for cluster voiceprints (caller owns its lifetime).</param>
    /// <param name="diarizer">Offline diarizer (caller owns its lifetime).</param>
    /// <param name="voiceprints">Enrolled voiceprints to resolve clusters against, or null
    /// to leave every cluster as an anonymous "Speaker N".</param>
    public static async Task<DiarizationResult?> AttributeAsync(
        string othersWavPath, AppConfig config, ISpeakerEmbedder embedder, SherpaDiarizer diarizer,
        IVoiceprintStore? voiceprints, CancellationToken ct)
    {
        if (!File.Exists(othersWavPath)) return null;
        var samples = SpeakerAudio.ReadWav16kMono(othersWavPath);
        if (samples.Length == 0) return null;

        var segments = diarizer.Process(samples);
        if (segments.Count == 0) return null;

        var resolver = new SpeakerResolver(voiceprints, config.VoiceprintMaxDistance);
        var clusters = new List<SpeakerCluster>();

        // Name clusters in order of first appearance so "Speaker 1" is the first to talk.
        foreach (var group in segments.GroupBy(s => s.Speaker).OrderBy(g => g.Min(s => s.Start)))
        {
            var mean = MeanEmbedding(embedder, samples, group);
            if (mean.Length == 0) continue;

            string name;
            var enrolled = false;
            if (voiceprints != null)
            {
                var match = await voiceprints.IdentifyAsync(mean, ct).ConfigureAwait(false);
                if (match is { } hit && hit.Distance <= config.VoiceprintMaxDistance)
                {
                    name = hit.PersonName;
                    enrolled = true;
                }
                else
                {
                    name = resolver.AssignSession(mean);
                }
            }
            else
            {
                name = resolver.AssignSession(mean);
            }

            clusters.Add(new SpeakerCluster(group.Key, name, enrolled, mean));
        }

        return clusters.Count == 0 ? null : new DiarizationResult(segments, clusters);
    }

    /// <summary>Average the embeddings of a cluster's turns into a single voiceprint.</summary>
    private static float[] MeanEmbedding(ISpeakerEmbedder embedder, float[] samples, IEnumerable<DiarizedSegment> turns)
    {
        float[]? sum = null;
        var count = 0;
        foreach (var turn in turns)
        {
            var embedding = embedder.Embed(SpeakerAudio.Slice(samples, turn.Start, turn.End));
            if (embedding.Length == 0) continue;
            if (sum == null)
            {
                sum = (float[])embedding.Clone();
            }
            else
            {
                for (var i = 0; i < sum.Length; i++) sum[i] += embedding[i];
            }
            count++;
        }
        if (sum == null) return [];
        for (var i = 0; i < sum.Length; i++) sum[i] /= count;
        return sum;
    }
}
