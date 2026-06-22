using CallScribe;
using CallScribe.Coach.Speaker;

// Measure how the offline diarizer's clustering threshold affects the discovered speaker
// count over a REAL recording, so the over-fragmentation fix (#26) is tuned against actual
// meeting audio rather than clean TTS. No mic/speakers needed: it reads a saved Others WAV.
//
//   dotnet run --project tools/DiarizeEval -- <path-to.others.wav> [threshold ...]
//
// With no thresholds given it sweeps a sensible default range. Higher threshold = coarser =
// fewer speakers. Per-cluster speech duration is printed so you can see whether the extra
// clusters are real people or short-utterance fragments.

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: DiarizeEval <path-to.others.wav> [threshold ...]");
    return 2;
}

var wavPath = args[0];
if (!File.Exists(wavPath))
{
    Console.Error.WriteLine($"Recording not found: {wavPath}");
    return 2;
}

var thresholds = args.Length > 1
    ? args[1..].Select(a => float.Parse(a)).ToArray()
    : [0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f];

var config = AppConfig.Load();
var segModel = SpeakerIdentity.ModelPath(config.SpeakerSegModel);
var embModel = SpeakerIdentity.ModelPath(config.SpeakerEmbedModel);
if (segModel == null || embModel == null)
{
    Console.Error.WriteLine("Speaker models not installed. Run scripts/coach-pull-speaker-models.ps1 first.");
    return 2;
}

var embedder = SpeakerIdentity.TryCreateEmbedder(config);
if (embedder == null)
{
    Console.Error.WriteLine("Could not load the speaker embedder (needed for the merge pass).");
    return 2;
}

var samples = SpeakerAudio.ReadWav16kMono(wavPath);
var totalSecs = samples.Length / 16000.0;
var minClusterSecs = config.DiarizationMinClusterSeconds;
Console.WriteLine($"Recording: {Path.GetFileName(wavPath)}  ({totalSecs:F0}s audio, {samples.Length} samples @16k)");
Console.WriteLine($"Sweeping clusterThreshold (higher = fewer, coarser speakers).");
Console.WriteLine($"'raw' = native diarizer clusters; 'merged' = after folding clusters < {minClusterSecs:F0}s into nearest:\n");

// More threads since this is an offline batch eval and the machine is otherwise idle.
const int numThreads = 4;

foreach (var threshold in thresholds)
{
    using var diarizer = new SherpaDiarizer(segModel, embModel, numThreads, numClusters: -1, clusterThreshold: threshold);
    var segments = diarizer.Process(samples);
    var rawCount = segments.Select(s => s.Speaker).Distinct().Count();

    var merged = OfflineDiarization.MergeSmallClusters(embedder, samples, segments, minClusterSecs);
    var mergedGroups = merged
        .GroupBy(s => s.Speaker)
        .Select(g => (Speaker: g.Key, Secs: g.Sum(s => s.End - s.Start)))
        .OrderByDescending(c => c.Secs)
        .ToList();

    var durs = string.Join("  ", mergedGroups.Select(c => $"{c.Secs:F0}s"));
    Console.WriteLine($"threshold {threshold:F2} -> raw {rawCount,2}  merged {mergedGroups.Count,2}   [{durs}]");
}

embedder.Dispose();
return 0;
