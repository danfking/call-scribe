using System.Threading.Channels;
using CallScribe.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Spectre.Console;
using Whisper.net;

namespace CallScribe.Transcription;

/// <summary>Live captions during recording. Taps each capture track's chunk stream,
/// accumulates audio until a natural pause (or a max window), runs a small fast
/// Whisper model over the chunk, and prints the caption. This is a preview: the
/// full-quality batch transcription at stop remains the artifact.</summary>
public sealed class LiveCaptionEngine : IDisposable
{
    private static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SilenceTail = TimeSpan.FromSeconds(0.6);
    // Loopback capture level scales with the device volume, so this must sit well
    // below quiet-listening levels while staying above digital silence.
    private const float SilenceRmsThreshold = 0.002f;

    // The mic track's captions are held briefly so loopback captions of the same
    // words (speaker bleed) can win: the Others copy is the correctly-labelled one.
    private static readonly TimeSpan MeQuarantine = TimeSpan.FromSeconds(2.5);

    private readonly WhisperFactory _factory;
    private readonly List<Task> _workers = [];
    private readonly List<Task> _pendingPrints = [];
    private readonly CrossTrackEchoFilter _echoFilter = new();
    private readonly object _consoleLock = new();

    public LiveCaptionEngine(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
    }

    public void Attach(string label, string colour, ChannelReader<AudioChunk> tap, WaveFormat sourceFormat)
    {
        _workers.Add(Task.Run(() => RunTrackAsync(label, colour, tap, sourceFormat)));
    }

    public async Task CompleteAsync()
    {
        await Task.WhenAll(_workers).ConfigureAwait(false);
        Task[] pending;
        lock (_pendingPrints) pending = [.. _pendingPrints];
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task RunTrackAsync(string label, string colour, ChannelReader<AudioChunk> tap, WaveFormat format)
    {
        // One processor per track: a WhisperProcessor is not safe for concurrent use.
        using var processor = _factory.CreateBuilder().WithLanguage("en").Build();
        var buffer = new MemoryStream();

        await foreach (var chunk in tap.ReadAllAsync().ConfigureAwait(false))
        {
            buffer.Write(chunk.Buffer, 0, chunk.Count);
            var buffered = TimeSpan.FromSeconds((double)buffer.Length / format.AverageBytesPerSecond);

            var atPause = buffered >= MinWindow && IsTrailingSilence(buffer, format);
            if (buffered >= MaxWindow || atPause)
            {
                await FlushAsync(processor, buffer, format, label, colour).ConfigureAwait(false);
            }
        }

        if (buffer.Length > format.AverageBytesPerSecond / 2)
        {
            await FlushAsync(processor, buffer, format, label, colour).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(WhisperProcessor processor, MemoryStream buffer, WaveFormat format, string label, string colour)
    {
        try
        {
            // Whisper hallucinates on pure silence; skip chunks with no audible content.
            if (PeakRms(buffer, format) < SilenceRmsThreshold) return;

            using var wav16k = ConvertTo16kMonoWav(buffer, format, PeakAmplitude(buffer, format));
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(wav16k).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0) parts.Add(text);
            }

            var caption = string.Join(" ", parts);
            if (caption.Length == 0 || IsNonSpeechAnnotation(caption)) return;

            var now = DateTime.Now;
            if (label == "Others")
            {
                // Loopback is authoritative: print immediately and register so
                // mic-side echoes of the same words can be suppressed.
                _echoFilter.Record(label, caption, now);
                Print(now, colour, label, caption);
            }
            else
            {
                // Mic captions wait briefly: if the same words arrive on loopback,
                // this was speaker bleed, not the user talking.
                var print = Task.Run(async () =>
                {
                    await Task.Delay(MeQuarantine).ConfigureAwait(false);
                    if (_echoFilter.IsEchoOfOtherTrack(label, caption, DateTime.Now)) return;
                    _echoFilter.Record(label, caption, DateTime.Now);
                    Print(now, colour, label, caption);
                });
                lock (_pendingPrints) _pendingPrints.Add(print);
            }
        }
        finally
        {
            buffer.SetLength(0);
        }
    }

    private void Print(DateTime at, string colour, string label, string caption)
    {
        lock (_consoleLock)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{at:HH:mm:ss}[/] [{colour}]{label,-6}[/] {caption.EscapeMarkup()}");
        }
    }

    /// <summary>Whisper marks non-speech audio with bracketed annotations like
    /// [MUSIC PLAYING], (wind blowing) or [BLANK_AUDIO]. Those are noise in a
    /// caption feed: drop captions that contain nothing else.</summary>
    private static bool IsNonSpeechAnnotation(string text)
    {
        var stripped = System.Text.RegularExpressions.Regex
            .Replace(text, @"[\[\(\*][^\]\)\*]*[\]\)\*]", "")
            .Trim(' ', '.', ',', '-');
        return stripped.Length == 0;
    }

    private static MemoryStream ConvertTo16kMonoWav(MemoryStream source, WaveFormat format, float peakAmplitude)
    {
        source.Position = 0;
        using var raw = new RawSourceWaveStream(source, format);
        ISampleProvider samples = raw.ToSampleProvider();
        if (format.Channels > 1) samples = samples.ToMono();

        // Loopback level follows the device volume and Whisper degrades on very
        // quiet audio: normalise the chunk towards full scale.
        var gain = peakAmplitude > 0 ? Math.Min(0.9f / peakAmplitude, 30f) : 1f;
        if (gain > 1.05f)
        {
            samples = new VolumeSampleProvider(samples) { Volume = gain };
        }

        var resampled = new WdlResamplingSampleProvider(samples, 16000).ToWaveProvider16();
        var output = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(output, resampled);
        output.Position = 0;
        return output;
    }

    /// <summary>Largest absolute sample value in the buffer (device format).</summary>
    private static float PeakAmplitude(MemoryStream buffer, WaveFormat format)
    {
        var data = buffer.GetBuffer();
        var end = (int)buffer.Length;
        var peak = 0f;

        if (format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < end; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data, i)));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < end; i += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data, i) / 32768f));
            }
        }
        return peak;
    }

    /// <summary>RMS over the trailing window: low energy = the speaker paused.</summary>
    private static bool IsTrailingSilence(MemoryStream buffer, WaveFormat format)
    {
        var tailBytes = (int)(format.AverageBytesPerSecond * SilenceTail.TotalSeconds);
        if (buffer.Length < tailBytes) return false;
        return Rms(buffer, format, (int)(buffer.Length - tailBytes), tailBytes) < SilenceRmsThreshold;
    }

    /// <summary>Max RMS across coarse slices, so a short utterance in a long buffer still counts.</summary>
    private static float PeakRms(MemoryStream buffer, WaveFormat format)
    {
        var sliceBytes = Math.Max(1, format.AverageBytesPerSecond / 2);
        var peak = 0f;
        for (var offset = 0; offset < buffer.Length; offset += sliceBytes)
        {
            var length = (int)Math.Min(sliceBytes, buffer.Length - offset);
            peak = Math.Max(peak, Rms(buffer, format, offset, length));
        }
        return peak;
    }

    private static float Rms(MemoryStream buffer, WaveFormat format, int offset, int byteCount)
    {
        var data = buffer.GetBuffer();
        double sum = 0;
        long count = 0;

        if (format.BitsPerSample == 32)
        {
            // WASAPI shared-mode captures deliver IEEE float.
            var floats = (byteCount / 4) * 4;
            for (var i = 0; i < floats; i += 4)
            {
                var sample = BitConverter.ToSingle(data, offset + i);
                sum += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var shorts = (byteCount / 2) * 2;
            for (var i = 0; i < shorts; i += 2)
            {
                var sample = BitConverter.ToInt16(data, offset + i) / 32768f;
                sum += sample * sample;
                count++;
            }
        }
        else
        {
            return float.MaxValue; // unknown format: never classify as silence
        }

        return count == 0 ? 0f : (float)Math.Sqrt(sum / count);
    }

    public void Dispose() => _factory.Dispose();
}
