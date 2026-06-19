using System.Globalization;
using CallScribe;
using CallScribe.Audio;
using CallScribe.Transcription;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

// Echo-bleed baseline harness for issue #6.
//
// Plays a far-side speech clip through the speakers at a sweep of device-volume
// levels, runs the live capture + caption pipeline with the mic silent, and
// tallies how much far-side speech leaks into the Me side. During far-side-only
// playback in a quiet room, any Me caption is bleed. Run this on SPEAKERS, not
// headphones, since acoustic bleed is the whole point.

// AEC mic-capture smoke test (issue #7). Records a few seconds from the Voice
// Capture DSP straight to a WAV. This mode plays NO audio: it only pulls cleaned
// mic input, so it is safe to run while sitting at the machine.
if (args.Length > 0 && args[0] == "aec-record")
{
    return await AecRecordAsync(args);
}

// AEC dual-capture comparison (issue #7). Plays the clip once while recording the
// raw mic and the AEC mic at the same time, then transcribes both directly,
// bypassing the echo filter, so we can see whether AEC removes the far-side bleed.
if (args.Length > 0 && args[0] == "aec-compare")
{
    return await AecCompareAsync(args);
}

string? clipPath = null;
var volumes = new List<float> { 0.10f, 0.25f, 0.50f, 0.75f, 1.00f };
var tailSeconds = 12.0;
var liveModel = "base.en";
var trials = 1;
var useAec = false;

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
        case "--trials": trials = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--aec": useAec = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

if (clipPath is null)
{
    Console.WriteLine("Usage: EchoHarness --clip <farside.mp3|wav> " +
                      "[--volumes 0.1,0.25,0.5,0.75,1.0] [--trials 1] [--aec] [--tail 12] [--live-model base.en]");
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
Console.WriteLine($"AEC:     {(useAec ? "ON (Voice Capture DSP)" : "off")}");
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

var results = new List<VolumeStats>();
try
{
    endpointVolume.Mute = false;
    foreach (var level in volumes)
    {
        Console.WriteLine();
        Console.WriteLine($"--- volume {level.ToString("P0", CultureInfo.InvariantCulture)} ({trials} trial(s)) ---");
        endpointVolume.MasterVolumeLevelScalar = level;
        await Task.Delay(400);

        var trialsWithLeak = 0;
        var totalBleed = 0;
        var samples = new List<string>();
        for (var t = 1; t <= trials; t++)
        {
            var r = await RunOneAsync(level);
            totalBleed += r.MeBleed;
            if (r.MeBleed > 0) { trialsWithLeak++; samples.AddRange(r.MeCaptions); }
            Console.WriteLine($"    trial {t}/{trials}: others={r.Others} me-bleed={r.MeBleed}");
        }
        results.Add(new VolumeStats(level, trials, trialsWithLeak, totalBleed, samples));
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
Console.WriteLine("volume   trials   leaked    rate   total-bleed");
foreach (var r in results)
{
    var rate = r.Trials == 0 ? 0 : (double)r.TrialsWithLeak / r.Trials;
    Console.WriteLine($"{r.Volume.ToString("P0", CultureInfo.InvariantCulture),6}   {r.Trials,6}   {r.TrialsWithLeak,6}   {rate.ToString("P0", CultureInfo.InvariantCulture),5}   {r.TotalBleed,10}");
}
Console.WriteLine();
foreach (var r in results.Where(r => r.SampleCaptions.Count > 0))
{
    Console.WriteLine($"Leaked Me captions at {r.Volume.ToString("P0", CultureInfo.InvariantCulture)}:");
    foreach (var c in r.SampleCaptions) Console.WriteLine($"  - {c}");
}

var csvPath = Path.Combine(AppPaths.RecordingsDir, "echo-baseline.csv");
await File.WriteAllLinesAsync(csvPath,
    new[] { "volume,trials,trials_with_leak,total_bleed" }.Concat(
        results.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Volume},{r.Trials},{r.TrialsWithLeak},{r.TotalBleed}"))));
Console.WriteLine();
Console.WriteLine($"Wrote {csvPath}");
return 0;

// Run one capture session at the given render volume: play the clip, observe captions.
async Task<RunResult> RunOneAsync(float level)
{
    var others = 0;
    var meCaptions = new List<string>();

    var stem = $"echo-harness-{(int)Math.Round(level * 100)}";
    using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config, useAec);
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

// Silent AEC capture smoke test: pull N seconds from VoiceCaptureAecSource into a
// WAV and report what came out. No playback.
static async Task<int> AecRecordAsync(string[] args)
{
    var outPath = Path.Combine(Path.GetTempPath(), "aec-smoke.wav");
    var seconds = 3;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out": outPath = args[++i]; break;
            case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
        }
    }

    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    Console.WriteLine("call-scribe AEC mic-capture smoke test (#7)");
    Console.WriteLine($"Output:  {outPath}");
    Console.WriteLine($"Seconds: {seconds}");
    Console.WriteLine("This mode plays no audio. It pulls echo-cancelled mic input only.");
    Console.WriteLine();

    long totalBytes = 0;
    var dataEvents = 0;
    Exception? stopError = null;
    var stopped = new TaskCompletionSource();

    using var source = new VoiceCaptureAecSource();
    await using var writer = new WaveFileWriter(outPath, source.WaveFormat);

    source.DataAvailable += (_, e) =>
    {
        Interlocked.Add(ref totalBytes, e.BytesRecorded);
        Interlocked.Increment(ref dataEvents);
        // WaveFileWriter is not thread-safe; the capture thread is the only writer here.
        writer.Write(e.Buffer, 0, e.BytesRecorded);
    };
    source.RecordingStopped += (_, e) =>
    {
        stopError = e.Exception;
        stopped.TrySetResult();
    };

    try
    {
        source.StartRecording();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"StartRecording threw: {ex}");
        return 2;
    }

    await Task.Delay(TimeSpan.FromSeconds(seconds));
    source.StopRecording();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await writer.FlushAsync();
    var fileSize = new FileInfo(outPath).Length;

    Console.WriteLine();
    Console.WriteLine("==== AEC smoke results ====");
    Console.WriteLine($"DataAvailable events: {dataEvents}");
    Console.WriteLine($"Bytes captured:       {Interlocked.Read(ref totalBytes)}");
    Console.WriteLine($"WAV file size:        {fileSize} bytes");
    Console.WriteLine($"Format:               {source.WaveFormat}");
    if (stopError is not null)
    {
        Console.Error.WriteLine($"RecordingStopped error: {stopError}");
        return 3;
    }
    if (totalBytes == 0)
    {
        Console.Error.WriteLine("No audio captured: ProcessOutput only ever returned S_FALSE / 0 bytes.");
        Console.Error.WriteLine("This may mean DEVICE_INDEXES must be set, or another init detail differs.");
        return 4;
    }
    return 0;
}

// Record raw mic and AEC mic simultaneously during one clip playback, then
// transcribe both. Same acoustic moment for both (no session variance) and the
// transcripts are read straight off the WAVs (no echo filter), so any far-side
// words in the AEC transcript are bleed the DSP failed to cancel.
static async Task<int> AecCompareAsync(string[] args)
{
    string? clipPath = null;
    var volume = 0.30f;
    var tailSeconds = 8.0;
    var liveModel = "base.en";
    var aes = 1;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--clip": clipPath = args[++i]; break;
            case "--volume": volume = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--tail": tailSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--live-model": liveModel = args[++i]; break;
            case "--aes": aes = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 1;
        }
    }
    if (clipPath is null || !File.Exists(clipPath))
    {
        Console.Error.WriteLine("Usage: EchoHarness aec-compare --clip <farside.mp3|wav> [--volume 0.3] [--tail 8] [--live-model base.en]");
        return 1;
    }
    var clip = clipPath;

    var modelPath = await ModelManager.EnsureWhisperModelAsync(
        ModelManager.ParseModel(liveModel), QuantizationType.NoQuantization);

    using var enumerator = new MMDeviceEnumerator();
    var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
    var micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    var mixFormat = renderDevice.AudioClient.MixFormat;
    var endpointVolume = renderDevice.AudioEndpointVolume;
    var originalVolume = endpointVolume.MasterVolumeLevelScalar;
    var originalMute = endpointVolume.Mute;

    Directory.CreateDirectory(AppPaths.RecordingsDir);
    var rawPath = Path.Combine(AppPaths.RecordingsDir, "aec-compare.raw.wav");
    var aecPath = Path.Combine(AppPaths.RecordingsDir, "aec-compare.aec.wav");

    Console.WriteLine("call-scribe AEC dual-capture comparison (#7)");
    Console.WriteLine($"Clip:   {clip}");
    Console.WriteLine($"Volume: {volume.ToString("P0", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Render: {renderDevice.FriendlyName}");
    Console.WriteLine($"Mic:    {micDevice.FriendlyName}");
    Console.WriteLine($"AES:    {aes} (residual echo suppressor; 0 = plain AEC)");
    Console.WriteLine();

    using var rawCapture = new WasapiCapture(micDevice);
    var rawWriter = new WaveFileWriter(rawPath, rawCapture.WaveFormat);
    var rawStopped = new TaskCompletionSource();
    rawCapture.DataAvailable += (_, e) => rawWriter.Write(e.Buffer, 0, e.BytesRecorded);
    rawCapture.RecordingStopped += (_, _) => { rawWriter.Dispose(); rawStopped.TrySetResult(); };

    using var aecSource = new VoiceCaptureAecSource { EchoSuppressionLevel = aes };
    var aecWriter = new WaveFileWriter(aecPath, aecSource.WaveFormat);
    var aecStopped = new TaskCompletionSource();
    aecSource.DataAvailable += (_, e) => aecWriter.Write(e.Buffer, 0, e.BytesRecorded);
    aecSource.RecordingStopped += (_, _) => aecStopped.TrySetResult();

    try
    {
        endpointVolume.Mute = false;
        endpointVolume.MasterVolumeLevelScalar = volume;
        await Task.Delay(400);

        rawCapture.StartRecording();
        aecSource.StartRecording();
        await Task.Delay(700); // let the AEC adaptive filter start converging

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

        await Task.Delay(TimeSpan.FromSeconds(tailSeconds));
    }
    finally
    {
        rawCapture.StopRecording();
        aecSource.StopRecording();
        endpointVolume.MasterVolumeLevelScalar = originalVolume;
        endpointVolume.Mute = originalMute;
    }

    await rawStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await aecStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    aecWriter.Dispose();

    using var factory = WhisperFactory.FromPath(modelPath);
    var rawText = await TranscribeWavAsync(factory, rawPath);
    var aecText = await TranscribeWavAsync(factory, aecPath);

    Console.WriteLine();
    Console.WriteLine("==== transcripts (echo filter bypassed) ====");
    Console.WriteLine("The far-side clip says: the integration catalog needs a performance pass before");
    Console.WriteLine("the release; whether the crossfire reels are ready to ship; line up the rollout.");
    Console.WriteLine();
    Console.WriteLine($"RAW mic [{rawCapture.WaveFormat}]:");
    Console.WriteLine($"  {(rawText.Length == 0 ? "(no speech)" : rawText)}");
    Console.WriteLine();
    Console.WriteLine($"AEC mic [{aecSource.WaveFormat}]:");
    Console.WriteLine($"  {(aecText.Length == 0 ? "(no speech)" : aecText)}");
    Console.WriteLine();
    Console.WriteLine($"WAVs: {rawPath}");
    Console.WriteLine($"      {aecPath}");
    return 0;
}

// Resample a WAV to 16 kHz mono and transcribe it with Whisper. Reads straight off
// the file, with no echo suppression in the path.
static async Task<string> TranscribeWavAsync(WhisperFactory factory, string wavPath)
{
    var tmp = Path.Combine(Path.GetTempPath(), $"aec-cmp-{Guid.NewGuid():N}.wav");
    try
    {
        using (var reader = new AudioFileReader(wavPath))
        using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 })
        {
            WaveFileWriter.CreateWaveFile(tmp, resampler);
        }

        using var processor = factory.CreateBuilder().WithLanguage("en").Build();
        using var fs = File.OpenRead(tmp);
        var parts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(fs))
        {
            var text = segment.Text.Trim();
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join(" ", parts);
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}

internal readonly record struct RunResult(float Volume, int Others, int MeBleed, List<string> MeCaptions);

internal readonly record struct VolumeStats(
    float Volume, int Trials, int TrialsWithLeak, int TotalBleed, List<string> SampleCaptions);
