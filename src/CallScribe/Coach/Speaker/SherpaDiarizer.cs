using SherpaOnnx;

namespace CallScribe.Coach.Speaker;

/// <summary>One contiguous stretch of a single speaker, in seconds from the start of the
/// audio. <see cref="Speaker"/> is a local cluster index (0,1,2…), not an identity.</summary>
public readonly record struct DiarizedSegment(double Start, double End, int Speaker);

/// <summary>Offline speaker diarization over a whole recording, backed by sherpa-onnx
/// (pyannote segmentation + an embedding model, clustered). "Offline" means it sees the
/// entire audio at once, so clustering is far more accurate than the live single-pass
/// guesser — this is the after-meeting authority. Speaker count is discovered from the
/// audio via a clustering threshold unless a fixed count is given.</summary>
public sealed class SherpaDiarizer : IDisposable
{
    private readonly OfflineSpeakerDiarization _diarization;

    public SherpaDiarizer(
        string segmentationModelPath, string embeddingModelPath,
        int numThreads = 1, int numClusters = -1, float clusterThreshold = 0.5f)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModelPath;
        config.Segmentation.NumThreads = numThreads;
        config.Embedding.Model = embeddingModelPath;
        config.Embedding.NumThreads = numThreads;
        // A positive NumClusters fixes the speaker count; otherwise the threshold lets the
        // clusterer discover it (higher threshold = fewer, coarser speakers).
        if (numClusters > 0) config.Clustering.NumClusters = numClusters;
        else config.Clustering.Threshold = clusterThreshold;

        _diarization = new OfflineSpeakerDiarization(config);
    }

    /// <summary>Sample rate the model expects; feed Process audio resampled to this.</summary>
    public int SampleRate => _diarization.SampleRate;

    public IReadOnlyList<DiarizedSegment> Process(float[] samples16kMono)
    {
        var segments = _diarization.Process(samples16kMono);
        var result = new DiarizedSegment[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            result[i] = new DiarizedSegment(segments[i].Start, segments[i].End, segments[i].Speaker);
        }
        return result;
    }

    public void Dispose() => _diarization.Dispose();
}
