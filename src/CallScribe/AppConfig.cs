using System.Text.Json;
using System.Text.Json.Serialization;

namespace CallScribe;

/// <summary>User configuration, stored at %APPDATA%\call-scribe\config.json.
/// Everything has a sensible default; the file only exists once the user sets something.</summary>
public sealed class AppConfig
{
    /// <summary>Friendly-name substring of the microphone to record (Me track).
    /// Null = default communications capture device.</summary>
    [JsonPropertyName("micDevice")]
    public string? MicDevice { get; set; }

    /// <summary>Friendly-name substring of the output device to loopback-record (Others track).
    /// Null = default communications render device.</summary>
    [JsonPropertyName("loopbackDevice")]
    public string? LoopbackDevice { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "large-v3-turbo";

    /// <summary>Small model for live captions during recording (tiny.en/base.en/small.en).
    /// small.en is the default: benchmarking showed it cuts live word-error materially over
    /// base.en for a little extra latency. Override per-run with <c>listen --live-model</c>.</summary>
    [JsonPropertyName("liveModel")]
    public string LiveModel { get; set; } = "small.en";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>Root for recordings/ and transcripts/. Null = %USERPROFILE%\call-scribe.
    /// Documents is deliberately not the default: it is commonly OneDrive-redirected
    /// and call recordings must not land in a synced folder unless the user opts in.</summary>
    [JsonPropertyName("outputRoot")]
    public string? OutputRoot { get; set; }

    /// <summary>Keep the WAV files after a successful transcription. Default true;
    /// set false to reclaim disk automatically (~11 MB per minute per call).</summary>
    [JsonPropertyName("keepAudio")]
    public bool KeepAudio { get; set; } = true;

    // --- Coach (experimental) -------------------------------------------------
    // Realtime meeting coach. Default off; everything runs locally via Ollama so
    // "nothing leaves your machine" still holds. Fields beyond CoachEnabled are
    // consumed by later phases (local inference, memory).

    /// <summary>Enable the coach panel by default on `listen` (same as passing --coach).</summary>
    [JsonPropertyName("coachEnabled")]
    public bool CoachEnabled { get; set; }

    /// <summary>Base URL of the local Ollama server.</summary>
    [JsonPropertyName("ollamaUrl")]
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Small low-latency model for per-utterance triage and quick advice.</summary>
    [JsonPropertyName("fastModel")]
    public string FastModel { get; set; } = "qwen3:4b";

    /// <summary>Larger model for background synthesis and end-of-meeting consolidation.</summary>
    [JsonPropertyName("reasoningModel")]
    public string ReasoningModel { get; set; } = "llama3.1:8b";

    /// <summary>Embedding model for semantic memory recall.</summary>
    [JsonPropertyName("embedModel")]
    public string EmbedModel { get; set; } = "nomic-embed-text";

    /// <summary>How long Ollama keeps a model resident in VRAM after a call (Ollama
    /// duration string, e.g. "10m", "1h", "0" to unload immediately). Keeping the fast
    /// model and embedder warm avoids reload latency between utterances.</summary>
    [JsonPropertyName("ollamaKeepAlive")]
    public string OllamaKeepAlive { get; set; } = "10m";

    /// <summary>Maximum cosine distance for a recalled memory to be shown to the advisor
    /// (smaller = stricter). nomic-embed relevant matches sit near 0.3; raise this to
    /// recall more loosely, lower it to keep only very close matches.</summary>
    [JsonPropertyName("coachRecallMaxDistance")]
    public double CoachRecallMaxDistance { get; set; } = 0.35;

    /// <summary>Npgsql connection string for the memory store (Postgres + Timescale + pgvector).</summary>
    [JsonPropertyName("postgresConn")]
    public string PostgresConn { get; set; } =
        "Host=localhost;Port=5432;Database=callscribe;Username=postgres;Password=postgres";

    // --- Speaker identification (experimental) --------------------------------
    // Tell apart and name the far-side speakers so the coach reasons per person.
    // Local acoustic embeddings (sherpa-onnx ONNX models); degrades to the plain
    // Me/Others labels when the models or native runtime are absent.

    /// <summary>Identify far-side speakers by voice (live labels + after-meeting attribution).
    /// Off by default; without it the coach sees the plain "Others" label as before.</summary>
    [JsonPropertyName("speakerIdEnabled")]
    public bool SpeakerIdEnabled { get; set; }

    /// <summary>Run offline diarization on the Others track after the meeting to attribute
    /// the saved transcript and consolidated memories. Authoritative over the live guesses.</summary>
    [JsonPropertyName("diarizeAfterMeeting")]
    public bool DiarizeAfterMeeting { get; set; } = true;

    /// <summary>Max cosine distance to accept an enrolled voiceprint as a match (smaller =
    /// stricter). Raise to recognise people more readily at the risk of confusing similar
    /// voices; lower to only auto-name confident matches.</summary>
    [JsonPropertyName("voiceprintMaxDistance")]
    public double VoiceprintMaxDistance { get; set; } = 0.30;

    /// <summary>Clustering threshold for the offline diarizer (higher = fewer, coarser
    /// speakers). The sherpa default of 0.5 over-fragments real meeting audio into dozens of
    /// short clusters; ~0.75 keeps genuinely distinct voices apart while collapsing the
    /// fragment tail. Tuned against real recordings (see tools/DiarizeEval).</summary>
    [JsonPropertyName("diarizationClusterThreshold")]
    public float DiarizationClusterThreshold { get; set; } = 0.75f;

    /// <summary>After diarization, any speaker cluster with less than this many seconds of
    /// speech is folded into its nearest substantial cluster by voiceprint, so a brief turn is
    /// attributed to the right person instead of spawning a new "Speaker N". 0 disables the
    /// merge.</summary>
    [JsonPropertyName("diarizationMinClusterSeconds")]
    public double DiarizationMinClusterSeconds { get; set; } = 8.0;

    /// <summary>Max cosine distance to merge a live far-side caption into an existing session
    /// speaker instead of minting a new "Speaker N". Looser than the enrolled-match threshold
    /// because clustering one meeting's voices is more forgiving than asserting an identity;
    /// raise it if one person fragments into several speakers live, lower it if distinct people
    /// get merged.</summary>
    [JsonPropertyName("sessionMergeDistance")]
    public double SessionMergeDistance { get; set; } = 0.70;

    /// <summary>A live far-side caption shorter than this many seconds is too brief to embed
    /// reliably, so it attaches to the nearest existing session speaker rather than minting a new
    /// one (short "yeah"/"okay" turns were the main source of live speaker fragmentation). 0
    /// disables the gate. Only affects live labels; the after-meeting pass is authoritative.</summary>
    [JsonPropertyName("liveMinSpeakerSeconds")]
    public double LiveMinSpeakerSeconds { get; set; } = 1.5;

    /// <summary>Max cosine distance for the after-meeting pass that folds the meeting's
    /// fragmented live speaker labels together. The live resolver clusters one clip at a time and
    /// can never un-split a person it once minted a second "Speaker N" for; once the meeting's
    /// audio is in, this pass re-examines all the (now-stable, averaged) session centroids and
    /// merges any pair within this distance, then rewrites the persisted live transcript. Looser
    /// than <see cref="SessionMergeDistance"/> on purpose, since averaged centroids are steadier
    /// than a single clip; lower it if distinct people get merged, 0 disables the pass.</summary>
    [JsonPropertyName("speakerConsolidationDistance")]
    public double SpeakerConsolidationDistance { get; set; } = 0.80;

    /// <summary>Minimum number of live clips for a session speaker to count as a real person that
    /// the consolidation pass protects; any label with fewer clips is treated as a fragment and
    /// folded into the nearest real speaker (within <see cref="SpeakerConsolidationDistance"/>).
    /// Raise it to fold more aggressively (more borderline clusters treated as fragments), lower it
    /// to protect more clusters from being merged away.</summary>
    [JsonPropertyName("speakerConsolidationMinClips")]
    public int SpeakerConsolidationMinClips { get; set; } = 3;

    /// <summary>Filename of the pyannote speaker-segmentation ONNX model under the models
    /// directory (used by offline diarization to find speech turns).</summary>
    [JsonPropertyName("speakerSegModel")]
    public string SpeakerSegModel { get; set; } = "sherpa-onnx-pyannote-segmentation-3-0.onnx";

    /// <summary>Filename of the speaker-embedding ONNX model under the models directory
    /// (turns a voice slice into a voiceprint). Default is an English TitaNet model.</summary>
    [JsonPropertyName("speakerEmbedModel")]
    public string SpeakerEmbedModel { get; set; } = "nemo_en_titanet_small.onnx";

    /// <summary>Your own enrolled voiceprint name (set by `coach enroll-me`). When set, the
    /// mic track is checked against it: captions in your voice are kept and labelled with
    /// this name; captions that clearly are not you (far-side bleed) are dropped. Null = no
    /// self check (mic captions stay "Me", guarded only by the text echo filter).</summary>
    [JsonPropertyName("selfSpeakerName")]
    public string? SelfSpeakerName { get; set; }

    /// <summary>Max cosine distance for a mic caption to count as your voice. Deliberately
    /// loose (live mic clips, especially with far-side bleed mixed in on speakers, sit well
    /// above clean-room distances) so your own speech is not dropped; raise it if your real
    /// speech still gets suppressed, lower it if far-side bleed prints as you.</summary>
    [JsonPropertyName("selfMatchMaxDistance")]
    public double SelfMatchMaxDistance { get; set; } = 0.6;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "call-scribe", "config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                $"Config file is not valid JSON: {ConfigPath}. Fix or delete it.");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
    }
}
