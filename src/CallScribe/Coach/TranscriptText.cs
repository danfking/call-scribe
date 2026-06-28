using System.Text;

namespace CallScribe.Coach;

/// <summary>Renders transcript lines for an LLM prompt: a "Transcript:" header followed by one
/// "Speaker: text" line each. Shared by the meeting consolidator, the speaker-name extractor, and the
/// coaching-profile updater so the prompt shape stays identical across them.</summary>
internal static class TranscriptText
{
    public static string ForPrompt(IEnumerable<(string Speaker, string Text)> lines)
    {
        var sb = new StringBuilder("Transcript:\n");
        foreach (var (speaker, text) in lines)
        {
            sb.Append(speaker).Append(": ").AppendLine(text);
        }
        return sb.ToString();
    }
}
