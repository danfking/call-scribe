using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace CallScribe.Transcription;

/// <summary>Transcribes one WAV track: resample to 16 kHz mono, VAD-gate the speech
/// regions (mirrors faster-whisper's vad_filter; prevents hallucination on the
/// silence-heavy mic track), then run Whisper over each speech region and offset
/// the timestamps back to track time.</summary>
public sealed class TrackTranscriber : IDisposable
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const int WavHeaderProbeBytes = 44; // not used for offsets; WaveFileReader handles framing

    private static readonly TimeSpan VadPadding = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan VadMergeGap = TimeSpan.FromSeconds(1.0);

    private readonly WhisperFactory _whisperFactory;
    private readonly WhisperProcessor _processor;
    private readonly WhisperVadFactory _vadFactory;
    private readonly WhisperVadProcessor _vad;

    public TrackTranscriber(string whisperModelPath, string vadModelPath, string language = "en")
    {
        _whisperFactory = WhisperFactory.FromPath(whisperModelPath);
        _processor = _whisperFactory.CreateBuilder()
            .WithLanguage(language)
            .Build();
        _vadFactory = WhisperVadFactory.FromPath(vadModelPath);
        _vad = _vadFactory.CreateBuilder()
            .WithThreshold(0.5f)
            .Build();
    }

    public async Task<TrackTranscript> TranscribeAsync(string wavPath, string trackName, CancellationToken ct = default)
    {
        var (samples, duration) = LoadAs16kMono(wavPath);

        var speechRegions = DetectSpeechRegions(samples, duration);
        var segments = new List<TranscriptSegment>();

        foreach (var (start, end) in speechRegions)
        {
            ct.ThrowIfCancellationRequested();
            using var regionStream = SliceToWavStream(samples, start, end);
            await foreach (var result in _processor.ProcessAsync(regionStream, ct).ConfigureAwait(false))
            {
                var text = result.Text.Trim();
                if (text.Length == 0) continue;
                segments.Add(new TranscriptSegment(
                    Math.Round((start + result.Start).TotalSeconds, 2),
                    Math.Round((start + result.End).TotalSeconds, 2),
                    text));
            }
        }

        return new TrackTranscript(trackName, Math.Round(duration.TotalSeconds, 2), segments);
    }

    /// <summary>Read any WAV and produce 16 kHz mono 16-bit PCM samples in memory.</summary>
    private static (byte[] Samples, TimeSpan Duration) LoadAs16kMono(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        ISampleProvider sampleProvider = reader.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels > 1)
        {
            sampleProvider = sampleProvider.ToMono();
        }
        var resampled = new WdlResamplingSampleProvider(sampleProvider, SampleRate).ToWaveProvider16();

        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = resampled.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        var samples = buffer.ToArray();
        var duration = TimeSpan.FromSeconds((double)samples.Length / (SampleRate * BytesPerSample));
        return (samples, duration);
    }

    /// <summary>VAD over the whole track, then pad and merge nearby speech segments so
    /// Whisper sees coherent utterances rather than clipped fragments.</summary>
    private List<(TimeSpan Start, TimeSpan End)> DetectSpeechRegions(byte[] samples, TimeSpan duration)
    {
        using var wavStream = ToWavStream(samples);
        var raw = _vad.DetectSpeechAsync(wavStream).GetAwaiter().GetResult();

        var regions = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var segment in raw)
        {
            var start = segment.Start - VadPadding;
            var end = segment.End + VadPadding;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (end > duration) end = duration;

            if (regions.Count > 0 && start - regions[^1].End < VadMergeGap)
            {
                regions[^1] = (regions[^1].Start, end);
            }
            else
            {
                regions.Add((start, end));
            }
        }
        return regions;
    }

    private static MemoryStream SliceToWavStream(byte[] samples, TimeSpan start, TimeSpan end)
    {
        var bytesPerSecond = SampleRate * BytesPerSample;
        var offset = AlignToSample((long)(start.TotalSeconds * bytesPerSecond));
        var length = AlignToSample((long)((end - start).TotalSeconds * bytesPerSecond));
        length = Math.Min(length, samples.Length - offset);
        return ToWavStream(samples, (int)offset, (int)length);
    }

    private static long AlignToSample(long byteCount) => byteCount - (byteCount % BytesPerSample);

    private static MemoryStream ToWavStream(byte[] samples, int offset = 0, int? length = null)
    {
        var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(stream), new WaveFormat(SampleRate, 16, 1)))
        {
            writer.Write(samples, offset, length ?? samples.Length);
        }
        stream.Seek(0, SeekOrigin.Begin);
        return stream;
    }

    public void Dispose()
    {
        _vad.Dispose();
        _vadFactory.Dispose();
        _processor.Dispose();
        _whisperFactory.Dispose();
    }

    /// <summary>Keeps the underlying stream open when WaveFileWriter is disposed.</summary>
    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* deliberately keep inner open */ }
    }
}
