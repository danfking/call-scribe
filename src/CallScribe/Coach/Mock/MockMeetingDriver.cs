using System.Text.Json;
using System.Text.Json.Serialization;
using CallScribe.Transcription;

namespace CallScribe.Coach.Mock;

/// <summary>Replays a scripted meeting into the coach and display, bypassing audio and
/// Whisper. This is the deterministic harness for verifying the ORPA pipeline
/// end-to-end. Script format is JSONL, one utterance per line:
/// <c>{"t": 1.5, "speaker": "Others", "text": "..."}</c> where <c>t</c> is seconds
/// from the start of the meeting and <c>speaker</c> is "Me" or "Others".</summary>
public static class MockMeetingDriver
{
    private sealed record ScriptLine(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("text")] string Text);

    public static async Task ReplayAsync(
        string scriptPath, LiveStatusDisplay display, CoachEngine coach,
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

            var (label, colour) =
                line.Speaker.Equals(LiveCaptionEngine.MeLabel, StringComparison.OrdinalIgnoreCase)
                    ? (LiveCaptionEngine.MeLabel, "cyan")
                    : (LiveCaptionEngine.OthersLabel, "yellow");

            var at = DateTime.Now;
            display.PrintCaption(at, colour, label, line.Text);
            coach.Observe(new CaptionEvent(at, label, line.Text));
        }
    }
}
