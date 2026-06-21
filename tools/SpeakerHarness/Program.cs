using CallScribe;
using CallScribe.Coach.Speaker;

// End-to-end check of the speaker-identification path WITHOUT a live call: synthesise
// distinct voices with Windows TTS (the far-side "Others" audio), then run the real
// sherpa-onnx embedder, diarizer, and resolver over them and confirm the speakers come
// out separated and consistent. The mic ("Me") path is not exercised — capture is known
// to work, and only the Others track is ever diarized.
//
//   dotnet run --project tools/SpeakerHarness
//
// Needs: the speaker models (scripts/coach-pull-speaker-models.ps1) and at least two
// installed SAPI voices (Windows ships David + Zira). TTS is driven through SAPI COM
// (SpVoice), which is more reliable than System.Speech on modern .NET.

var work = Path.Combine(Path.GetTempPath(), "call-scribe-speaker-harness");
Directory.CreateDirectory(work);

var config = new AppConfig();
var embedder = SpeakerIdentity.TryCreateEmbedder(config);
using var diarizer = SpeakerIdentity.TryCreateDiarizer(config);
if (embedder == null || diarizer == null)
{
    Console.Error.WriteLine("Speaker models not installed. Run scripts/coach-pull-speaker-models.ps1 first.");
    return 2;
}

var voices = Tts.GetVoices();
if (voices.Count < 2)
{
    Console.Error.WriteLine($"Need at least two installed TTS voices; found {voices.Count}.");
    return 2;
}
var voiceA = voices[0];
var voiceB = voices[1];
Console.WriteLine($"Voices: A = {voiceA.Name}, B = {voiceB.Name}");
Console.WriteLine($"Embedding model dim: {embedder.Dimensions}\n");

// Four utterances: two per voice, different sentences, so we measure voice identity,
// not the words. These stand in for two far-side participants each speaking twice.
var clips = new (string Tag, Tts.Voice Voice, string Text, string Path)[]
{
    ("A1", voiceA, "The quarterly revenue numbers look strong across every region this period.", Path.Combine(work, "a1.wav")),
    ("A2", voiceA, "Let us schedule the contract review for next Tuesday afternoon if that works.", Path.Combine(work, "a2.wav")),
    ("B1", voiceB, "I disagree on that point, we really should prioritise the security review first.", Path.Combine(work, "b1.wav")),
    ("B2", voiceB, "Could you send across the latest deployment metrics before the call tomorrow?", Path.Combine(work, "b2.wav")),
};
foreach (var clip in clips) Tts.Synthesize(clip.Voice, clip.Text, clip.Path);

var samples = clips.ToDictionary(c => c.Tag, c => SpeakerAudio.ReadWav16kMono(c.Path));
var embeddings = clips.ToDictionary(c => c.Tag, c => embedder.Embed(samples[c.Tag]));
foreach (var clip in clips)
{
    var secs = samples[clip.Tag].Length / 16000.0;
    Console.WriteLine($"  {clip.Tag} ({clip.Voice.Name}): {secs:F1}s audio, embedding {embeddings[clip.Tag].Length} dims");
}

var pass = true;

// --- 1. Embedding discrimination: same voice closer than different voice ----------
Console.WriteLine("\n[1] Embedding cosine distances (smaller = more similar voice):");
double D(string x, string y) => VectorMath.CosineDistance(embeddings[x], embeddings[y]);
var sameA = D("A1", "A2");
var sameB = D("B1", "B2");
var cross = new[] { D("A1", "B1"), D("A1", "B2"), D("A2", "B1"), D("A2", "B2") };
Console.WriteLine($"    same voice A (A1-A2): {sameA:F3}");
Console.WriteLine($"    same voice B (B1-B2): {sameB:F3}");
Console.WriteLine($"    cross A-B (min..max): {cross.Min():F3} .. {cross.Max():F3}");
var discriminates = Math.Max(sameA, sameB) < cross.Min();
Report("same-voice clips are closer than any cross-voice pair", discriminates);
pass &= discriminates;

// --- 2. Session clustering: the resolver assigns consistent, distinct labels ------
Console.WriteLine("\n[2] SpeakerResolver session clustering over [A1, B1, A2, B2]:");
var resolver = new SpeakerResolver(store: null); // no enrolled voiceprints: pure clustering
var lblA1 = resolver.AssignSession(embeddings["A1"]);
var lblB1 = resolver.AssignSession(embeddings["B1"]);
var lblA2 = resolver.AssignSession(embeddings["A2"]);
var lblB2 = resolver.AssignSession(embeddings["B2"]);
Console.WriteLine($"    A1 -> {lblA1}");
Console.WriteLine($"    B1 -> {lblB1}");
Console.WriteLine($"    A2 -> {lblA2}");
Console.WriteLine($"    B2 -> {lblB2}");
var clustersOk = lblA1 == lblA2 && lblB1 == lblB2 && lblA1 != lblB1;
Report("voice A clips share a label, voice B clips share a different label", clustersOk);
pass &= clustersOk;

// --- 3. Offline diarization over a concatenated multi-speaker recording -----------
// A: 0..lenA, B: lenA..lenA+lenB, A again: ...   (one mixed "Others" track)
Console.WriteLine("\n[3] Offline diarization of a concatenated A|B|A recording:");
var timeline = new List<(string Tag, double Start, double End)>();
var mixed = new List<float>();
foreach (var tag in new[] { "A1", "B1", "A2" })
{
    var start = mixed.Count / 16000.0;
    mixed.AddRange(samples[tag]);
    mixed.AddRange(new float[16000 / 2]); // 0.5s gap between turns
    timeline.Add((tag, start, mixed.Count / 16000.0));
}
foreach (var t in timeline) Console.WriteLine($"    truth: {t.Tag} at {t.Start:F1}..{t.End:F1}s");

var segments = diarizer.Process([.. mixed]);
Console.WriteLine($"    diarizer produced {segments.Count} turns:");
foreach (var s in segments) Console.WriteLine($"      speaker_{s.Speaker}  {s.Start:F1}..{s.End:F1}s");
var clusterCount = segments.Select(s => s.Speaker).Distinct().Count();
var twoSpeakers = clusterCount == 2;
Report($"found exactly 2 distinct speakers (got {clusterCount})", twoSpeakers);
pass &= twoSpeakers;

// The first and middle turns are different people; the first and last are the same.
if (segments.Count > 0)
{
    int ClusterAt(double t) => segments
        .Where(s => t >= s.Start && t <= s.End)
        .Select(s => s.Speaker)
        .DefaultIfEmpty(-1).First();
    var firstTurn = ClusterAt((timeline[0].Start + timeline[0].End) / 2);
    var midTurn = ClusterAt((timeline[1].Start + timeline[1].End) / 2);
    var lastTurn = ClusterAt((timeline[2].Start + timeline[2].End) / 2);
    Console.WriteLine($"    cluster at A-turn-1={firstTurn}, B-turn={midTurn}, A-turn-2={lastTurn}");
    var ordering = firstTurn != -1 && firstTurn == lastTurn && firstTurn != midTurn;
    Report("A's two turns share a cluster, B's differs", ordering);
    pass &= ordering;
}

// --- 4. Voiceprint persistence: enroll one voice, then identify across voices -----
// Exercises the real pgvector VoiceprintStore (EnrollAsync running-mean + IdentifyAsync
// cosine search). Skipped (not failed) when Postgres is unreachable.
Console.WriteLine("\n[4] Voiceprint store enroll/identify (pgvector):");
var voiceprints = await SpeakerIdentity.TryCreateVoiceprintsAsync(config, embedder.Dimensions, CancellationToken.None);
if (voiceprints == null)
{
    Console.WriteLine("    SKIP: Postgres not reachable (run scripts/coach-pull-models.ps1 to start the DB).");
}
else
{
    await using (voiceprints)
    {
        const string person = "Harness-David";
        await voiceprints.ForgetAsync(person, CancellationToken.None); // clean slate for re-runs
        // Enroll voice A from one clip; then identify using the *other* A clip (held out)
        // and a B clip, with a fresh resolver, exactly as the live path does.
        await voiceprints.EnrollAsync(person, embeddings["A1"], CancellationToken.None);

        var idSameVoice = await voiceprints.IdentifyAsync(embeddings["A2"], CancellationToken.None);
        var idOtherVoice = await voiceprints.IdentifyAsync(embeddings["B1"], CancellationToken.None);
        Console.WriteLine($"    identify held-out A2 -> {idSameVoice?.PersonName} @ {idSameVoice?.Distance:F3}");
        Console.WriteLine($"    identify B1         -> {idOtherVoice?.PersonName} @ {idOtherVoice?.Distance:F3}");

        // The resolver accepts a match only within VoiceprintMaxDistance (default 0.30).
        var sameAccepted = idSameVoice is { } a && a.PersonName == person && a.Distance <= config.VoiceprintMaxDistance;
        var otherRejected = idOtherVoice is not { } b || b.Distance > config.VoiceprintMaxDistance;
        Report($"enrolled voice recognised across clips (<= {config.VoiceprintMaxDistance})", sameAccepted);
        Report("different voice is NOT accepted as the enrolled person", otherRejected);
        pass &= sameAccepted && otherRejected;

        await voiceprints.ForgetAsync(person, CancellationToken.None);
    }
}

Console.WriteLine();
Console.WriteLine(pass ? "RESULT: PASS — speakers are identified correctly." : "RESULT: FAIL — see checks above.");
embedder.Dispose();
return pass ? 0 : 1;

static void Report(string label, bool ok) =>
    Console.WriteLine($"    => {(ok ? "PASS" : "FAIL")}: {label}");

/// <summary>Windows TTS via SAPI COM (SpVoice), used to fabricate distinct far-side voices.
/// System.Speech's SelectVoice throws on modern .NET, so we drive SAPI directly.</summary>
static class Tts
{
    public sealed record Voice(string Name, object Token);

    public static List<Voice> GetVoices()
    {
        dynamic spVoice = NewCom("SAPI.SpVoice");
        dynamic tokens = spVoice.GetVoices();
        var list = new List<Voice>();
        for (var i = 0; i < (int)tokens.Count; i++)
        {
            dynamic token = tokens.Item(i);
            list.Add(new Voice((string)token.GetDescription(), token));
        }
        return list;
    }

    public static void Synthesize(Voice voice, string text, string path)
    {
        dynamic spVoice = NewCom("SAPI.SpVoice");
        spVoice.Voice = voice.Token;
        dynamic stream = NewCom("SAPI.SpFileStream");
        stream.Open(path, 3 /* SSFMCreateForWrite */, false);
        spVoice.AudioOutputStream = stream;
        spVoice.Speak(text, 0 /* SVSFDefault: synchronous */);
        stream.Close();
    }

    private static object NewCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"COM type {progId} not registered."))!;
}
