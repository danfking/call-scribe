using System.Text.Json;
using System.Text.Json.Serialization;
using CallScribe.Transcription;

namespace CallScribe.Coach.Mock;

/// <summary>Replays a scripted meeting into a caption observer and the display, bypassing audio
/// and Whisper. This is the deterministic harness for verifying caption-consuming pipelines
/// (the coach's ORPA loop, the RPG engine) end-to-end. Script format is JSONL, one utterance
/// per line: <c>{"t": 1.5, "speaker": "Others", "text": "..."}</c> where <c>t</c> is seconds
/// from the start of the meeting. <c>speaker</c> is "Me", "Others", or a far-side
/// person's name (e.g. "Gavin"): a name is carried through as the resolved speaker so
/// named multi-party flows can be exercised without speaker-id audio.</summary>
public static class MockMeetingDriver
{
    private sealed record ScriptLine(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("text")] string Text);

    public static async Task ReplayAsync(
        string scriptPath, LiveStatusDisplay display, Action<CaptionEvent> observe,
        bool realtime, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(scriptPath, ct).ConfigureAwait(false);
        var script = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ScriptLine>(line)
                            ?? throw new InvalidDataException($"Invalid script line: {line}"))
            .OrderBy(line => line.T)
            .ToList();

        var origin = DateTime.Now;
        foreach (var line in script)
        {
            ct.ThrowIfCancellationRequested();
            if (realtime)
            {
                var wait = origin.AddSeconds(line.T) - DateTime.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var isMe = line.Speaker.Equals(LiveCaptionEngine.MeLabel, StringComparison.OrdinalIgnoreCase);
            var label = isMe ? LiveCaptionEngine.MeLabel : LiveCaptionEngine.OthersLabel;
            var colour = isMe ? "cyan" : "yellow";
            // A named far-side speaker is carried as the resolved Speaker; "Me"/"Others" are
            // plain channels with no resolved name.
            var speaker = isMe || line.Speaker.Equals(LiveCaptionEngine.OthersLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : line.Speaker;

            var at = DateTime.Now;
            display.PrintCaption(at, colour, speaker ?? label, line.Text);
            observe(new CaptionEvent(at, label, line.Text, speaker));
        }
    }
}
