using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>One capture device feeding one WAV file via a channel.
///
/// The channel is the seam for live transcription later: anything can read the
/// chunks as they flow. The writer pads silence gaps with zeros against a shared
/// stopwatch epoch, because WASAPI loopback delivers nothing at all while the
/// system is silent and the two tracks must stay time-aligned for the merge.</summary>
public sealed class CaptureTrack : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly Channel<AudioChunk> _channel;
    private readonly List<ChannelWriter<AudioChunk>> _taps = [];
    private readonly Stopwatch _epoch;
    private readonly string _outputPath;
    private Task<TimeSpan>? _writerTask;
    private readonly TaskCompletionSource _stopped = new();

    public string Name { get; }
    public WaveFormat WaveFormat => _capture.WaveFormat;
    public ChannelReader<AudioChunk> Chunks => _channel.Reader;

    /// <summary>Set when capture died mid-recording (e.g. the device was unplugged).
    /// The WAV is still finalised with whatever was captured.</summary>
    public Exception? Error { get; private set; }

    public CaptureTrack(string name, IWaveIn capture, Stopwatch sharedEpoch, string outputPath)
    {
        Name = name;
        _capture = capture;
        _epoch = sharedEpoch;
        _outputPath = outputPath;
        _channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _capture.DataAvailable += (_, e) =>
        {
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            var chunk = new AudioChunk(copy, e.BytesRecorded);
            _channel.Writer.TryWrite(chunk);
            foreach (var tap in _taps) tap.TryWrite(chunk);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            _channel.Writer.TryComplete(e.Exception);
            foreach (var tap in _taps) tap.TryComplete();
            _stopped.TrySetResult();
        };
    }

    /// <summary>Subscribe a secondary consumer (e.g. live captions) to the audio stream.
    /// Chunks are shared, never copied per-tap. Call before Start().</summary>
    public ChannelReader<AudioChunk> AddTap()
    {
        var channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true });
        _taps.Add(channel.Writer);
        return channel.Reader;
    }

    public void Start()
    {
        _writerTask = Task.Run(WriteLoopAsync);
        _capture.StartRecording();
    }

    public async Task<TimeSpan> StopAsync()
    {
        _capture.StopRecording();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var written = await (_writerTask ?? Task.FromResult(TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        return written;
    }

    private async Task<TimeSpan> WriteLoopAsync()
    {
        var format = _capture.WaveFormat;
        long bytesWritten = 0;
        // Pad only when the writer is clearly behind the wall clock; small jitter is normal.
        var toleranceBytes = format.AverageBytesPerSecond / 4; // 250 ms
        var blockAlign = format.BlockAlign;

        await using var writer = new WaveFileWriter(_outputPath, format);
        try
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var expectedBytes = (long)(_epoch.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
                var gap = expectedBytes - (bytesWritten + chunk.Count);
                if (gap > toleranceBytes)
                {
                    var padBytes = gap - (gap % blockAlign);
                    await WriteZerosAsync(writer, padBytes).ConfigureAwait(false);
                    bytesWritten += padBytes;
                }

                await writer.WriteAsync(chunk.Buffer.AsMemory(0, chunk.Count)).ConfigureAwait(false);
                bytesWritten += chunk.Count;
            }
        }
        catch (Exception ex)
        {
            // Capture died (device unplugged, driver reset). Keep what we have:
            // the writer disposes cleanly below, so the WAV stays playable.
            Error = ex is System.Threading.Channels.ChannelClosedException { InnerException: not null } closed
                ? closed.InnerException
                : ex;
        }

        // Final pad so both tracks end at the same wall-clock instant.
        var finalExpected = (long)(_epoch.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
        var tailGap = finalExpected - bytesWritten;
        if (tailGap > blockAlign)
        {
            var padBytes = tailGap - (tailGap % blockAlign);
            await WriteZerosAsync(writer, padBytes).ConfigureAwait(false);
            bytesWritten += padBytes;
        }

        return TimeSpan.FromSeconds((double)bytesWritten / format.AverageBytesPerSecond);
    }

    private static async Task WriteZerosAsync(WaveFileWriter writer, long count)
    {
        var zeros = new byte[Math.Min(count, 64 * 1024)];
        var remaining = count;
        while (remaining > 0)
        {
            var slice = (int)Math.Min(remaining, zeros.Length);
            await writer.WriteAsync(zeros.AsMemory(0, slice)).ConfigureAwait(false);
            remaining -= slice;
        }
    }

    public void Dispose() => _capture.Dispose();
}
