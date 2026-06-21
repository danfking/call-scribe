namespace CallScribe.Coach.Speaker;

/// <summary>Turns a slice of speech into a fixed-length speaker embedding (a voiceprint):
/// a vector that captures voice identity, not words. Two utterances from the same person
/// land close together by cosine distance; different people land apart. Backed locally by
/// an ONNX model (sherpa-onnx) so nothing leaves the machine. The embedding dimension is
/// the model's, and is unrelated to the text-embedding dimension used for memory recall.</summary>
public interface ISpeakerEmbedder : IDisposable
{
    /// <summary>Vector length the model produces; must match the voiceprint column.</summary>
    int Dimensions { get; }

    /// <summary>Embed 16 kHz mono PCM samples in [-1, 1]. Returns a zero-length array if the
    /// slice is too short or silent to characterise a voice.</summary>
    float[] Embed(ReadOnlySpan<float> samples16kMono);
}
