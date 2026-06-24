using System.Threading.Channels;
using CallScribe.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace CallScribe.Transcription;

/// <summary>Live captions during recording. Taps each capture track's chunk stream,
/// accumulates audio until a natural pause (or a max window), runs a small fast
/// Whisper model over the chunk, and prints the caption. This is a preview: the
/// full-quality batch transcription at stop remains the artifact.
///
/// Speaker-bleed suppression: on speakers the mic hears the other side, so the same
/// words surface on both tracks. Each caption carries the wall-clock span of audio
/// it came from. Me captions are held until the Others track has resolved past
/// their span (its caption for the same audio exists, or its loopback was silent),
/// then dropped if an Others caption with an overlapping span says the same thing.
/// Others captions are authoritative and never suppressed.</summary>
public sealed class LiveCaptionEngine : IDisposable
{
    private static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SilenceTail = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan FrontierMargin = TimeSpan.FromSeconds(1);
    // The Me-caption hold trades latency for catching far-side bleed. Keep it short
    // so the live preview stays responsive; the accurate transcript at stop is
    // unaffected. With continuous far-side audio a Me caption prints after at most
    // this long instead of waiting indefinitely.
    private static readonly TimeSpan FrontierTimeout = TimeSpan.FromSeconds(6);

    // Speaker resolution (embed + voiceprint DB lookup) runs inline on the Others worker;
    // cap it so a slow or hung database can't stall the live caption preview. On timeout we
    // fall back to the plain label — the after-meeting pass is the authoritative attribution.
    private static readonly TimeSpan SpeakerResolveTimeout = TimeSpan.FromSeconds(2);

    // Loopback capture level scales with the device volume, so this must sit well
    // below quiet-listening levels while staying above digital silence. Used for the Others
    // (loopback) track and for trailing-pause detection.
    private const float SilenceRmsThreshold = 0.002f;

    // The mic ("Me") track needs a higher bar than the loopback-silence threshold: a live but
    // muted mic (muted in the meeting app, or just idle) still delivers continuous near-silent
    // audio whose noise floor sits above SilenceRmsThreshold, which made the Me track churn
    // Listening/Transcribing while the user was not speaking. Measured mic levels put real speech
    // well above 0.01 and idle/noise-floor below it. Overridable via config (LiveMeSpeechThreshold).
    private const double DefaultMeSpeechThreshold = 0.01;

    public const string OthersLabel = "Others";
    public const string MeLabel = "Me";

    private readonly double _meSpeechThreshold;
    private readonly WhisperFactory _factory;
    private readonly LiveStatusDisplay _display = new();
    private readonly CrossTrackEchoFilter _echoFilter = new();
    private readonly List<Task> _workers = [];
    private readonly List<Task> _pendingDecisions = [];

    /// <summary>Start of the Others track's unresolved audio, as ticks.
    /// long.MaxValue = nothing unresolved (its buffer is empty and no chunk is being
    /// transcribed), meaning the track is resolved up to the present moment. A silent
    /// loopback cannot produce bleed, so idle counts as fully resolved.</summary>
    private long _othersUnresolvedFromTicks = long.MaxValue;

    /// <summary>Raised when a caption is printed: Others as soon as it is transcribed,
    /// Me only after it survives echo suppression. Suppressed bleed never fires this.
    /// Lets a harness or test observe what actually reached the screen.</summary>
    public event Action<CaptionEvent>? CaptionEmitted;

    /// <summary>Optional far-side speaker resolver: given the 16 kHz mono samples of an
    /// Others caption and a cancellation token, returns the name to attribute it to (a known
    /// person or "Speaker N"). Null = no speaker identification, so Others captions keep the
    /// generic "Others" label. Awaited inline on the Others worker (so it adds to that
    /// caption's latency) but bounded by <see cref="SpeakerResolveTimeout"/>.</summary>
    public Func<float[], CancellationToken, Task<string>>? ResolveOthersSpeaker { get; set; }

    /// <summary>Optional self-voice check for mic captions: given the 16 kHz mono samples and
    /// a token, decides whether the caption is the user's own voice (keep, optionally with a
    /// name to display instead of "Me") or far-side bleed (suppress). Null = no check, so mic
    /// captions stay "Me", guarded only by the text echo filter. Runs on the deferred Me
    /// decision, bounded by <see cref="SpeakerResolveTimeout"/>.</summary>
    public Func<float[], CancellationToken, Task<MeSpeakerResult>>? IdentifyMeSpeaker { get; set; }

    public LiveCaptionEngine(string modelPath, double meSpeechThreshold = DefaultMeSpeechThreshold)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _meSpeechThreshold = meSpeechThreshold;
    }

    /// <summary>Minimum peak RMS for a buffer on this track to count as speech (vs silence/noise
    /// floor). The mic track sits above the noise floor; the loopback track keeps the low
    /// silence threshold so quiet far-side bleed is still caught for echo suppression.</summary>
    private double GateFor(string label) => label == MeLabel ? _meSpeechThreshold : SilenceRmsThreshold;

    public void Attach(string label, string colour, ChannelReader<AudioChunk> tap, WaveFormat sourceFormat)
    {
        _display.Register(label, colour);
        _workers.Add(Task.Run(() => RunTrackAsync(label, colour, tap, sourceFormat)));
    }

    /// <summary>Set dashboard detail (the live model name shown in the footer).</summary>
    public void ConfigureDisplay(string model) => _display.Configure(model);

    /// <summary>Turn on the coach advice column to the right of the transcript.</summary>
    public void EnableAdvicePanel() => _display.EnableAdvicePanel();

    /// <summary>Forward a coach advice item to the dashboard. Presentation hints are
    /// passed as primitives so this class stays independent of the coach namespace.</summary>
    public void PrintAdvice(DateTime at, string colour, string glyph, string text) =>
        _display.PrintAdvice(at, colour, glyph, text);

    /// <summary>Forward the coach's current activity (thinking / listening / nothing-to-add) to
    /// the dashboard status line. Primitives keep this class independent of the coach namespace.</summary>
    public void SetCoachActivity(string text, string colour) =>
        _display.SetCoachActivity(text, colour);

    /// <summary>Callback for the dashboard's /assign-name command (rename + persist a speaker).</summary>
    public Func<string, string, CancellationToken, Task<bool>>? OnAssignName
    {
        get => _display.OnAssignName;
        set => _display.OnAssignName = value;
    }

    /// <summary>Callback for the dashboard's /ask command (answer a question about the transcript).</summary>
    public Func<string, string, CancellationToken, Task<string>>? OnAsk
    {
        get => _display.OnAsk;
        set => _display.OnAsk = value;
    }

    /// <summary>Wait until the user ends the session from the dashboard (/stop or Esc), or a
    /// line on stdin when output is redirected.</summary>
    public Task WaitForStopAsync(CancellationToken ct) => _display.WaitForStopAsync(ct);

    public async Task CompleteAsync()
    {
        await Task.WhenAll(_workers).ConfigureAwait(false);
        Task[] pending;
        lock (_pendingDecisions) pending = [.. _pendingDecisions];
        await Task.WhenAll(pending).ConfigureAwait(false);
        _display.Shutdown();
    }

    private async Task RunTrackAsync(string label, string colour, ChannelReader<AudioChunk> tap, WaveFormat format)
    {
        // One processor per track: a WhisperProcessor is not safe for concurrent use.
        using var processor = _factory.CreateBuilder().WithLanguage("en").Build();
        var buffer = new MemoryStream();
        var spanStart = DateTime.Now;
        var gate = GateFor(label);
        var heard = false; // has audio in the current buffer crossed the gate yet

        await foreach (var chunk in tap.ReadAllAsync().ConfigureAwait(false))
        {
            if (buffer.Length == 0)
            {
                spanStart = DateTime.Now;
                heard = false;
                if (label == OthersLabel) SetOthersUnresolvedFrom(spanStart);
            }
            buffer.Write(chunk.Buffer, 0, chunk.Count);

            // Show the track as active ("Hearing") only once incoming audio crosses its gate, so a
            // muted or idle mic (continuous near-silent chunks) stays "Listening" instead of
            // churning to Hearing on every buffer while the user is not speaking.
            if (!heard && Rms(chunk.Buffer.AsSpan(0, chunk.Count), format) >= gate)
            {
                heard = true;
                _display.SetState(label, TrackState.Hearing);
            }

            var buffered = TimeSpan.FromSeconds((double)buffer.Length / format.AverageBytesPerSecond);
            var atPause = buffered >= MinWindow && IsTrailingSilence(buffer, format);
            if (buffered >= MaxWindow || atPause)
            {
                await FlushAsync(processor, buffer, format, label, colour, spanStart).ConfigureAwait(false);
            }
        }

        // End of stream: flush any trailing audio regardless of size. A short final
        // Others fragment must still be transcribed and recorded into the echo filter
        // before SetOthersResolved() runs, otherwise an overlapping Me caption finds no
        // Others entry to match and prints the bleed un-suppressed. FlushAsync's own
        // guards (silence RMS and non-speech annotations) still decide what to keep.
        if (buffer.Length > 0)
        {
            await FlushAsync(processor, buffer, format, label, colour, spanStart).ConfigureAwait(false);
        }
        if (label == OthersLabel) SetOthersResolved();
        _display.SetState(label, TrackState.Listening);
    }

    private async Task FlushAsync(
        WhisperProcessor processor, MemoryStream buffer, WaveFormat format,
        string label, string colour, DateTime spanStart)
    {
        var spanEnd = DateTime.Now;
        try
        {
            // Whisper hallucinates on pure silence; skip buffers below the track's speech gate
            // (the mic gate is above its noise floor, so a muted/idle mic never transcribes).
            // Residual limitation: when the loopback signal sits below its threshold but the mic's
            // gain-normalised copy still transcribes (low speaker volume), the span resolves with no
            // Others caption, so genuine bleed can still print as Me (the speakers-vs-headphones case).
            if (PeakRms(buffer, format) < GateFor(label)) return;

            _display.SetState(label, TrackState.Transcribing);
            using var wav16k = ConvertTo16kMonoWav(buffer, format, PeakAmplitude(buffer, format));
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(wav16k).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0) parts.Add(text);
            }

            var caption = string.Join(" ", parts);
            if (caption.Length == 0 || IsNonSpeechAnnotation(caption)) return;

            if (label == OthersLabel)
            {
                // Loopback is authoritative: record so mic-side echoes can be
                // suppressed, then print immediately. Identify the far-side speaker (if
                // enabled) so the caption is attributed to a name, not just "Others".
                _echoFilter.Record(label, caption, spanStart, spanEnd);
                var speaker = await ResolveSpeakerAsync(buffer, format).ConfigureAwait(false);
                _display.PrintCaption(spanStart, colour, speaker ?? label, caption);
                CaptionEmitted?.Invoke(new CaptionEvent(spanStart, label, caption, speaker));
            }
            else
            {
                // Capture the mic samples now (before the buffer is cleared in finally) so the
                // deferred decision can voiceprint-check whether this is really the user.
                var samples = IdentifyMeSpeaker != null
                    ? ExtractSamples16kMono(buffer, format, PeakAmplitude(buffer, format))
                    : null;
                ScheduleMeDecision(caption, colour, spanStart, spanEnd, samples);
            }
        }
        finally
        {
            buffer.SetLength(0);
            if (label == OthersLabel) SetOthersResolved();
            _display.SetState(label, TrackState.Listening);
        }
    }

    /// <summary>Hold a Me caption until the Others track has resolved past its span
    /// (or a safety timeout), then print unless it turns out to be bleed — first by text
    /// similarity to the far side, then (if a self-voice check is set) by voiceprint.</summary>
    private void ScheduleMeDecision(string caption, string colour, DateTime spanStart, DateTime spanEnd, float[]? samples)
    {
        var decision = Task.Run(async () =>
        {
            var deadline = DateTime.Now + FrontierTimeout;
            while (OthersResolvedUntil() < spanEnd + FrontierMargin && DateTime.Now < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }

            if (_echoFilter.IsEchoOfOtherTrack(MeLabel, caption, spanStart, spanEnd)) return;

            // Voiceprint check: drop captions whose voice clearly isn't the user (far-side
            // bleed the text filter missed), and label confirmed ones with the user's name.
            string? speaker = null;
            var verify = IdentifyMeSpeaker;
            if (verify != null && samples != null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(SpeakerResolveTimeout);
                    var verdict = await verify(samples, cts.Token).ConfigureAwait(false);
                    if (verdict.IsBleed) return;
                    speaker = verdict.Name;
                }
                catch { /* best-effort: keep the caption as Me */ }
            }

            _display.PrintCaption(spanStart, colour, speaker ?? MeLabel, caption);
            CaptionEmitted?.Invoke(new CaptionEvent(spanStart, MeLabel, caption, speaker));
        });
        lock (_pendingDecisions) _pendingDecisions.Add(decision);
    }

    /// <summary>Resolve the far-side speaker for the current Others buffer, or null when no
    /// resolver is set or it fails (caller then keeps the generic label). Reads the same
    /// buffer that fed Whisper, before it is cleared in FlushAsync's finally.</summary>
    private async Task<string?> ResolveSpeakerAsync(MemoryStream buffer, WaveFormat format)
    {
        var resolve = ResolveOthersSpeaker;
        if (resolve == null) return null;
        try
        {
            var samples = ExtractSamples16kMono(buffer, format, PeakAmplitude(buffer, format));
            using var cts = new CancellationTokenSource(SpeakerResolveTimeout);
            return await resolve(samples, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private DateTime OthersResolvedUntil()
    {
        var ticks = Interlocked.Read(ref _othersUnresolvedFromTicks);
        return ticks == long.MaxValue ? DateTime.Now : new DateTime(ticks);
    }

    private void SetOthersUnresolvedFrom(DateTime from) =>
        Interlocked.Exchange(ref _othersUnresolvedFromTicks, from.Ticks);

    private void SetOthersResolved() =>
        Interlocked.Exchange(ref _othersUnresolvedFromTicks, long.MaxValue);

    /// <summary>Whisper marks non-speech audio with bracketed annotations like
    /// [MUSIC PLAYING], (wind blowing) or [BLANK_AUDIO]. Those are noise in a
    /// caption feed: drop captions that contain nothing else.</summary>
    internal static bool IsNonSpeechAnnotation(string text)
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

    /// <summary>Like <see cref="ConvertTo16kMonoWav"/> but yields the raw 16 kHz mono
    /// samples (for speaker embedding) rather than a WAV stream.</summary>
    private static float[] ExtractSamples16kMono(MemoryStream source, WaveFormat format, float peakAmplitude)
    {
        source.Position = 0;
        using var raw = new RawSourceWaveStream(source, format);
        ISampleProvider samples = raw.ToSampleProvider();
        if (format.Channels > 1) samples = samples.ToMono();

        var gain = peakAmplitude > 0 ? Math.Min(0.9f / peakAmplitude, 30f) : 1f;
        if (gain > 1.05f) samples = new VolumeSampleProvider(samples) { Volume = gain };

        var resampled = new WdlResamplingSampleProvider(samples, 16000);
        var all = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return [.. all];
    }

    /// <summary>RMS over the trailing window: low energy = the speaker paused.</summary>
    internal static bool IsTrailingSilence(MemoryStream buffer, WaveFormat format)
    {
        var tailBytes = (int)(format.AverageBytesPerSecond * SilenceTail.TotalSeconds);
        if (buffer.Length < tailBytes) return false;
        var tail = buffer.GetBuffer().AsSpan((int)(buffer.Length - tailBytes), tailBytes);
        return Rms(tail, format) < SilenceRmsThreshold;
    }

    /// <summary>Max RMS across coarse slices, so a short utterance in a long buffer still counts.</summary>
    internal static float PeakRms(MemoryStream buffer, WaveFormat format)
    {
        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var sliceBytes = Math.Max(1, format.AverageBytesPerSecond / 2);
        var peak = 0f;
        for (var offset = 0; offset < data.Length; offset += sliceBytes)
        {
            var length = Math.Min(sliceBytes, data.Length - offset);
            peak = Math.Max(peak, Rms(data.Slice(offset, length), format));
        }
        return peak;
    }

    /// <summary>Largest absolute sample value in the buffer (device format).</summary>
    internal static float PeakAmplitude(MemoryStream buffer, WaveFormat format)
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

    /// <summary>RMS of a PCM span (IEEE float or 16-bit), normalised to [0,1]. Unknown formats
    /// return float.MaxValue so they are never classified as silence.</summary>
    internal static float Rms(ReadOnlySpan<byte> data, WaveFormat format)
    {
        double sum = 0;
        long count = 0;

        if (format.BitsPerSample == 32)
        {
            // WASAPI shared-mode captures deliver IEEE float.
            for (var i = 0; i + 4 <= data.Length; i += 4)
            {
                var sample = BitConverter.ToSingle(data.Slice(i, 4));
                sum += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= data.Length; i += 2)
            {
                var sample = BitConverter.ToInt16(data.Slice(i, 2)) / 32768f;
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

    public void Dispose()
    {
        _display.Shutdown();
        _factory.Dispose();
    }
}
