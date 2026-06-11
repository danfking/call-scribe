using System.Text.Json;
using System.Text.Json.Serialization;

namespace CallScribe.Transcription;

/// <summary>JSON shapes matching the original prototype's transcribe.py output,
/// so transcripts from either pipeline are interchangeable.</summary>
public sealed record TranscriptSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);

public sealed record TrackTranscript(
    [property: JsonPropertyName("track")] string Track,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static TrackTranscript Load(string path) =>
        JsonSerializer.Deserialize<TrackTranscript>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Invalid transcript JSON: {path}");
}
