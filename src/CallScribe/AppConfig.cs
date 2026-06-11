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
