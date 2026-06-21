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

    /// <summary>Filename of the pyannote speaker-segmentation ONNX model under the models
    /// directory (used by offline diarization to find speech turns).</summary>
    [JsonPropertyName("speakerSegModel")]
    public string SpeakerSegModel { get; set; } = "sherpa-onnx-pyannote-segmentation-3-0.onnx";

    /// <summary>Filename of the speaker-embedding ONNX model under the models directory
    /// (turns a voice slice into a voiceprint). Default is an English TitaNet model.</summary>
    [JsonPropertyName("speakerEmbedModel")]
    public string SpeakerEmbedModel { get; set; } = "nemo_en_titanet_small.onnx";

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
