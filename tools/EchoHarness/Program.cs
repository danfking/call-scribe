using System.Globalization;
using CallScribe;
using CallScribe.Audio;
using CallScribe.Transcription;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net.Ggml;

// Echo-bleed baseline harness for issue #6.
//
// Plays a far-side speech clip through the speakers at a sweep of device-volume
// levels, runs the live capture + caption pipeline with the mic silent, and
// tallies how much far-side speech leaks into the Me side. During far-side-only
// playback in a quiet room, any Me caption is bleed. Run this on SPEAKERS, not
// headphones, since acoustic bleed is the whole point.

string? clipPath = null;
var volumes = new List<float> { 0.10f, 0.25f, 0.50f, 0.75f, 1.00f };
var tailSeconds = 12.0;
var liveModel = "base.en";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--clip": clipPath = args[++i]; break;
        case "--volumes":
            volumes = args[++i]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                .ToList();
            break;
        case "--tail": tailSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--live-model": liveModel = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (clipPath is null)
{
    Console.WriteLine("Usage: EchoHarness --clip <farside.mp3|wav> " +
                      "[--volumes 0.1,0.25,0.5,0.75,1.0] [--tail 12] [--live-model base.en]");
    return 1;
}
if (!File.Exists(clipPath))
{
    Console.Error.WriteLine($"Clip not found: {clipPath}");
    return 1;
}
var clip = clipPath;

Console.WriteLine("call-scribe echo-bleed baseline harness (#6)");
Console.WriteLine($"Clip:    {clip}");
Console.WriteLine($"Volumes: {string.Join(", ", volumes.Select(v => v.ToString("P0", CultureInfo.InvariantCulture)))}");
Console.WriteLine($"Model:   {liveModel}");
Console.WriteLine();
Console.WriteLine("Use SPEAKERS (not headphones). Keep the room quiet and do not speak during the run.");
Console.WriteLine("Any Me caption reported below is far-side bleed leaking into your track.");
Console.WriteLine();

var config = AppConfig.Load();
var modelPath = await ModelManager.EnsureWhisperModelAsync(
    ModelManager.ParseModel(liveModel), QuantizationType.NoQuantization);

using var enumerator = new MMDeviceEnumerator();
var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
var mixFormat = renderDevice.AudioClient.MixFormat;
var endpointVolume = renderDevice.AudioEndpointVolume;
var originalVolume = endpointVolume.MasterVolumeLevelScalar;
var originalMute = endpointVolume.Mute;
Console.WriteLine($"Render device: {renderDevice.FriendlyName} (volume will be swept, then restored)");

var results = new List<RunResult>();
try
{
    endpointVolume.Mute = false;
    foreach (var level in volumes)
    {
        Console.WriteLine();
        Console.WriteLine($"--- volume {level.ToString("P0", CultureInfo.InvariantCulture)} ---");
        endpointVolume.MasterVolumeLevelScalar = level;
        await Task.Delay(400);

        var result = await RunOneAsync(level);
        results.Add(result);
        Console.WriteLine($"    others={result.Others}  ME BLEED={result.MeBleed}");
    }
}
finally
{
    endpointVolume.MasterVolumeLevelScalar = originalVolume;
    endpointVolume.Mute = originalMute;
    Console.WriteLine();
    Console.WriteLine($"Restored render volume to {originalVolume.ToString("P0", CultureInfo.InvariantCulture)}.");
}

Console.WriteLine();
Console.WriteLine("==== baseline results ====");
Console.WriteLine("volume   others   me-bleed");
foreach (var r in results)
{
    Console.WriteLine($"{r.Volume.ToString("P0", CultureInfo.InvariantCulture),6}   {r.Others,6}   {r.MeBleed,8}");
}
Console.WriteLine();
foreach (var r in results.Where(r => r.MeBleed > 0))
{
    Console.WriteLine($"Me captions at {r.Volume.ToString("P0", CultureInfo.InvariantCulture)} (bleed):");
    foreach (var c in r.MeCaptions) Console.WriteLine($"  - {c}");
}

var csvPath = Path.Combine(AppPaths.RecordingsDir, "echo-baseline.csv");
await File.WriteAllLinesAsync(csvPath,
    new[] { "volume,others,me_bleed" }.Concat(
        results.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Volume},{r.Others},{r.MeBleed}"))));
Console.WriteLine();
Console.WriteLine($"Wrote {csvPath}");
return 0;

// Run one capture session at the given render volume: play the clip, observe captions.
async Task<RunResult> RunOneAsync(float level)
{
    var others = 0;
    var meCaptions = new List<string>();

    var stem = $"echo-harness-{(int)Math.Round(level * 100)}";
    using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config);
    using var captions = new LiveCaptionEngine(modelPath);
    captions.CaptionEmitted += ev =>
    {
        if (ev.Label == LiveCaptionEngine.MeLabel)
        {
            lock (meCaptions) meCaptions.Add(ev.Caption);
        }
        else
        {
            Interlocked.Increment(ref others);
        }
    };

    captions.Attach(LiveCaptionEngine.OthersLabel, "yellow", engine.OthersTrack.AddTap(), engine.OthersTrack.WaveFormat);
    captions.Attach(LiveCaptionEngine.MeLabel, "cyan", engine.MeTrack.AddTap(), engine.MeTrack.WaveFormat);

    engine.Start();

    using (var reader = new AudioFileReader(clip))
    using (var resampler = new MediaFoundationResampler(reader, mixFormat) { ResamplerQuality = 60 })
    using (var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, false, 100))
    {
        output.Init(resampler);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            await Task.Delay(100);
        }
    }

    // Let the pipeline drain: it buffers up to ~8s and the Me decision waits on the frontier.
    await Task.Delay(TimeSpan.FromSeconds(tailSeconds));

    await engine.StopAsync();
    await captions.CompleteAsync();

    lock (meCaptions)
    {
        return new RunResult(level, others, meCaptions.Count, [.. meCaptions]);
    }
}

internal readonly record struct RunResult(float Volume, int Others, int MeBleed, List<string> MeCaptions);
