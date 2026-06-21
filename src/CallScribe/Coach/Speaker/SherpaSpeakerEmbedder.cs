using SherpaOnnx;

namespace CallScribe.Coach.Speaker;

/// <summary>Speaker embedder backed by a local sherpa-onnx ONNX model. Wraps the
/// SpeakerEmbeddingExtractor: feed it 16 kHz mono samples and it returns a voiceprint.
/// The native runtime ships with the org.k2fsa.sherpa.onnx package; the .onnx model file
/// is downloaded separately (see scripts/coach-pull-speaker-models.ps1).</summary>
public sealed class SherpaSpeakerEmbedder : ISpeakerEmbedder
{
    // Below ~0.4s there is too little voiced audio to characterise a speaker reliably.
    private const int MinSamples = 16000 * 2 / 5;

    private readonly SpeakerEmbeddingExtractor _extractor;

    public int Dimensions { get; }

    public SherpaSpeakerEmbedder(string modelPath, int numThreads = 1)
    {
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = numThreads,
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
        Dimensions = _extractor.Dim;
    }

    public float[] Embed(ReadOnlySpan<float> samples16kMono)
    {
        if (samples16kMono.Length < MinSamples) return [];

        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(16000, samples16kMono.ToArray());
        stream.InputFinished();
        if (!_extractor.IsReady(stream)) return [];
        return _extractor.Compute(stream);
    }

    public void Dispose() => _extractor.Dispose();
}
