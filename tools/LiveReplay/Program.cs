using System.Text.Json;
using System.Threading.Channels;
using CallScribe;
using CallScribe.Audio;
using CallScribe.Coach.Speaker;
using CallScribe.Transcription;
using NAudio.Wave;
using Whisper.net.Ggml;

// Re-produce the LIVE transcript for a recorded meeting by feeding its saved WAVs back through the
// real LiveCaptionEngine, under a chosen live model and speaker thresholds. The engine windows by
// buffered AUDIO duration (not wall-clock), so the chunk boundaries — and thus the transcript — match
// a real live run. Lets us A/B live-pipeline changes (model, clustering) against a fixture and
// reconcile the result with TranscriptReconcile. (Wall-clock timestamps are compressed, so the
// timing dimension of a replay is not faithful — use it for transcription and speakers.)
//
//   dotnet run --project tools/LiveReplay -- --stem 2026-06-23-0931 --live-model small.en [--session-merge 0.7]

string? stem = null, liveModel = null, outPath = null;
double? merge = null, minSpeaker = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--stem": stem = args[++i]; break;
        case "--live-model": liveModel = args[++i]; break;
        case "--session-merge": merge = double.Parse(args[++i]); break;
        case "--min-speaker-seconds": minSpeaker = double.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 2;
    }
}
if (stem is null)
{
    Console.Error.WriteLine("Usage: LiveReplay --stem <stem> [--live-model base.en] [--session-merge d] [--min-speaker-seconds d] [--out file]");
    return 2;
}

var config = AppConfig.Load();
if (config.OutputRoot != null) AppPaths.OutputRootOverride = config.OutputRoot;
liveModel ??= "base.en";
if (merge is { } mg) config.SessionMergeDistance = mg;
if (minSpeaker is { } ms) config.LiveMinSpeakerSeconds = ms;

var othersWav = Path.Combine(AppPaths.RecordingsDir, stem + ".others.wav");
var meWav = Path.Combine(AppPaths.RecordingsDir, stem + ".me.wav");
if (!File.Exists(othersWav) || !File.Exists(meWav))
{
    Console.Error.WriteLine($"Recordings not found: {othersWav} / {meWav}");
    return 2;
}

var modelPath = await ModelManager.EnsureWhisperModelAsync(
    ModelManager.ParseModel(liveModel), QuantizationType.NoQuantization, CancellationToken.None);
Console.WriteLine($"live model {liveModel}; sessionMerge {config.SessionMergeDistance}; minSpeakerSeconds {config.LiveMinSpeakerSeconds}");

var speakerId = await SpeakerIdentity.TryCreateAsync(config, CancellationToken.None);
Console.WriteLine(speakerId is null ? "speaker-id OFF (labels stay Me/Others)" : "speaker-id ON");

using var captions = new LiveCaptionEngine(modelPath);
if (speakerId is not null)
{
    captions.ResolveOthersSpeaker = (samples, token) => speakerId.ResolveAsync(samples, token);
    captions.IdentifyMeSpeaker = (samples, token) => speakerId.VerifyMeAsync(samples, token);
}
var collected = new List<CaptionEvent>();
var gate = new Lock();
captions.CaptionEmitted += e => { lock (gate) collected.Add(e); };
captions.ConfigureDisplay(liveModel);

// Feed both tracks in lockstep at the same speed-up so their captions interleave in the right
// chronological order (the engine times segments by audio duration, so this only fixes ordering,
// not segmentation). 8x keeps a full meeting to ~1-2 minutes while preserving cross-track order.
const double Speedup = 8.0;
var othersReader = FeedWav(othersWav, Speedup, out var othersFormat);
var meReader = FeedWav(meWav, Speedup, out var meFormat);
captions.Attach(LiveCaptionEngine.OthersLabel, "yellow", othersReader, othersFormat);
captions.Attach(LiveCaptionEngine.MeLabel, "cyan", meReader, meFormat);

await captions.CompleteAsync();
if (speakerId is not null) await speakerId.DisposeAsync();

var ordered = collected.OrderBy(e => e.At).ToList();
var t0 = ordered.Count > 0 ? ordered[0].At : DateTime.Now;
var lines = ordered.Select(e => new ReplayLine((e.At - t0).TotalSeconds, e.SpeakerName, e.Caption));
outPath ??= Path.Combine(AppPaths.TranscriptsDir, $"{stem}.live.{liveModel}.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(lines, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"replayed live transcript: {ordered.Count} lines -> {outPath}");
return 0;

// Stream a WAV's PCM into a channel in fine ~20ms AudioChunks (so the engine sees near-continuous
// arrival like real WASAPI capture, and flushes at the same audio boundaries it would live), paced
// at audio-rate/speedup via a stopwatch (both tracks share the clock, so they stay in lockstep and
// captions interleave chronologically). Completes when the file ends.
static ChannelReader<AudioChunk> FeedWav(string path, double speedup, out WaveFormat format)
{
    var reader = new WaveFileReader(path);
    format = reader.WaveFormat;
    var channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true });
    var bytesPerChunk = Math.Max(1, format.AverageBytesPerSecond / 50); // ~20ms of audio
    var msPerChunk = 1000.0 * bytesPerChunk / format.AverageBytesPerSecond;
    _ = Task.Run(async () =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var audioMsFed = 0.0;
        try
        {
            var buffer = new byte[bytesPerChunk];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var copy = new byte[read];
                Array.Copy(buffer, copy, read);
                await channel.Writer.WriteAsync(new AudioChunk(copy, read)).ConfigureAwait(false);
                audioMsFed += msPerChunk;
                var behindMs = audioMsFed / speedup - clock.Elapsed.TotalMilliseconds;
                if (behindMs > 5) await Task.Delay((int)behindMs).ConfigureAwait(false);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            reader.Dispose();
        }
    });
    return channel.Reader;
}

internal readonly record struct ReplayLine(double sec, string speaker, string text);
