using CallScribe.Coach.Speaker;

namespace CallScribe.Tests;

public class OfflineDiarizationTests
{
    /// <summary>Fake embedder: returns the first three samples of a slice as the voiceprint, so a
    /// test can plant a known signature at the start of each speaker turn and control distances.</summary>
    private sealed class FirstThreeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => 3;
        public float[] Embed(ReadOnlySpan<float> samples) => samples.Length >= 3 ? [samples[0], samples[1], samples[2]] : [];
        public void Dispose() { }
    }

    // 16 kHz: turn at second N starts at sample N*16000. Plant a 3-vector signature there.
    private static float[] BuildSamples(int totalSeconds, params (int Second, float[] Sig)[] signatures)
    {
        var samples = new float[totalSeconds * 16000];
        foreach (var (second, sig) in signatures)
        {
            for (var i = 0; i < sig.Length; i++) samples[second * 16000 + i] = sig[i];
        }
        return samples;
    }

    [Fact]
    public void MergeSmallClusters_FoldsShortClusterIntoNearestSubstantialOne()
    {
        // A and B are substantial (10s each) and distinct; F is a 1s fragment close to A's voice.
        var samples = BuildSamples(21,
            (0, [1f, 0f, 0f]),       // cluster 0 (A)
            (10, [0f, 1f, 0f]),      // cluster 1 (B)
            (20, [0.9f, 0.1f, 0f])); // cluster 2 (F): near A
        var segments = new List<DiarizedSegment> { new(0, 10, 0), new(10, 20, 1), new(20, 21, 2) };

        var (merged, _) = OfflineDiarization.MergeSmallClusters(new FirstThreeEmbedder(), samples, segments, minClusterSeconds: 8.0);

        Assert.Equal(2, merged.Select(s => s.Speaker).Distinct().Count());
        Assert.Equal(0, merged.Single(s => s.Start == 20).Speaker); // F folded into A, not its own speaker
    }

    [Fact]
    public void MergeSmallClusters_ReturnsMeansForSurvivingClusters_SoCallerNeedNotReEmbed()
    {
        var samples = BuildSamples(21, (0, [1f, 0f, 0f]), (10, [0f, 1f, 0f]), (20, [0.9f, 0.1f, 0f]));
        var segments = new List<DiarizedSegment> { new(0, 10, 0), new(10, 20, 1), new(20, 21, 2) };

        var (merged, means) = OfflineDiarization.MergeSmallClusters(new FirstThreeEmbedder(), samples, segments, minClusterSeconds: 8.0);

        // Every surviving cluster has a precomputed mean the naming loop can reuse.
        foreach (var speaker in merged.Select(s => s.Speaker).Distinct())
        {
            Assert.True(means.ContainsKey(speaker));
        }
    }

    [Fact]
    public void MergeSmallClusters_IsNoOp_WhenGateDisabled()
    {
        var samples = BuildSamples(21, (0, [1f, 0f, 0f]), (10, [0f, 1f, 0f]), (20, [0.9f, 0.1f, 0f]));
        var segments = new List<DiarizedSegment> { new(0, 10, 0), new(10, 20, 1), new(20, 21, 2) };

        var (merged, _) = OfflineDiarization.MergeSmallClusters(new FirstThreeEmbedder(), samples, segments, minClusterSeconds: 0);

        Assert.Equal(3, merged.Select(s => s.Speaker).Distinct().Count());
    }

    [Fact]
    public void MergeSmallClusters_DropsUnembeddableNoiseFragment()
    {
        // Cluster 2 is a sub-sample-length blip the embedder can't characterise; it should be
        // dropped (not survive as a phantom speaker), leaving only the two real voices.
        var samples = BuildSamples(21, (0, [1f, 0f, 0f]), (10, [0f, 1f, 0f]));
        var segments = new List<DiarizedSegment> { new(0, 10, 0), new(10, 20, 1), new(20, 20.0001, 2) };

        var (merged, _) = OfflineDiarization.MergeSmallClusters(new FirstThreeEmbedder(), samples, segments, minClusterSeconds: 8.0);

        Assert.Equal(2, merged.Select(s => s.Speaker).Distinct().Count());
        Assert.DoesNotContain(merged, s => s.Speaker == 2);
    }

    [Fact]
    public void MergeSmallClusters_LeavesAllClustersAlone_WhenAllAreSubstantial()
    {
        var samples = BuildSamples(20, (0, [1f, 0f, 0f]), (10, [0f, 1f, 0f]));
        var segments = new List<DiarizedSegment> { new(0, 10, 0), new(10, 20, 1) };

        var (merged, _) = OfflineDiarization.MergeSmallClusters(new FirstThreeEmbedder(), samples, segments, minClusterSeconds: 8.0);

        Assert.Equal(2, merged.Select(s => s.Speaker).Distinct().Count());
    }
}
