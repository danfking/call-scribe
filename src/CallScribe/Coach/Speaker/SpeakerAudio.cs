using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CallScribe.Coach.Speaker;

/// <summary>Loads audio into the 16 kHz mono float samples the speaker models expect.
/// Any WAV (device-format stereo/float/48k) is resampled and downmixed.</summary>
public static class SpeakerAudio
{
    public const int SampleRate = 16000;

    public static float[] ReadWav16kMono(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (reader.WaveFormat.Channels > 1) provider = provider.ToMono();
        if (reader.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }
        return ReadAll(provider);
    }

    /// <summary>Samples for the half-open time window [start, end) in seconds, clamped to
    /// the available range. Used to isolate one diarized speaker turn for embedding.</summary>
    public static ReadOnlySpan<float> Slice(float[] samples16kMono, double startSeconds, double endSeconds)
    {
        var from = Math.Clamp((int)(startSeconds * SampleRate), 0, samples16kMono.Length);
        var to = Math.Clamp((int)(endSeconds * SampleRate), from, samples16kMono.Length);
        return samples16kMono.AsSpan(from, to - from);
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var all = new List<float>();
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return [.. all];
    }
}
