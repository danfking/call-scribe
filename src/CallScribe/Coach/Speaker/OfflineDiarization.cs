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

        // Fold short fragment clusters into their nearest substantial cluster so a brief turn is
        // attributed to the right person rather than spawning its own "Speaker N".
        segments = MergeSmallClusters(embedder, samples, segments, config.DiarizationMinClusterSeconds);

        var resolver = new SpeakerResolver(voiceprints, config.VoiceprintMaxDistance, config.SessionMergeDistance);
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

    /// <summary>Reassign the segments of every cluster with less than
    /// <paramref name="minClusterSeconds"/> of total speech to the substantial cluster whose mean
    /// voiceprint is nearest. This collapses the fragment tail that diarization leaves behind
    /// (short turns embed unreliably and otherwise become their own speakers) while keeping the
    /// short turn's words, now attributed to the most similar real speaker. No-op when the gate
    /// is disabled (≤ 0), or when there are no substantial clusters to merge into. Public for the
    /// DiarizeEval tool and unit tests.</summary>
    public static IReadOnlyList<DiarizedSegment> MergeSmallClusters(
        ISpeakerEmbedder embedder, float[] samples, IReadOnlyList<DiarizedSegment> segments, double minClusterSeconds)
    {
        if (minClusterSeconds <= 0) return segments;

        var stats = segments
            .GroupBy(s => s.Speaker)
            .Select(c => (Index: c.Key, Secs: c.Sum(s => s.End - s.Start), Mean: MeanEmbedding(embedder, samples, c)))
            .ToList();

        // A cluster is trustworthy only if it has enough speech AND embeds to a voiceprint.
        var substantial = stats.Where(c => c.Secs >= minClusterSeconds && c.Mean.Length > 0).ToList();
        if (substantial.Count == 0) return segments; // nothing to merge into

        var remap = new Dictionary<int, int>();
        var drop = new HashSet<int>();
        foreach (var c in stats)
        {
            if (c.Secs >= minClusterSeconds && c.Mean.Length > 0) continue; // keep substantial clusters
            if (c.Mean.Length > 0)
            {
                // Short but embeddable: fold into the substantial cluster nearest by voice.
                remap[c.Index] = substantial.OrderBy(s => VectorMath.CosineDistance(s.Mean, c.Mean)).First().Index;
            }
            else
            {
                // Too short/quiet to characterise (a sub-second blip): drop it as noise rather
                // than let it survive as a phantom speaker. Overlapping transcript text, if any,
                // falls back to the generic "Others" label.
                drop.Add(c.Index);
            }
        }

        if (remap.Count == 0 && drop.Count == 0) return segments;

        return [.. segments
            .Where(s => !drop.Contains(s.Speaker))
            .Select(s => remap.TryGetValue(s.Speaker, out var to) ? s with { Speaker = to } : s)];
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
