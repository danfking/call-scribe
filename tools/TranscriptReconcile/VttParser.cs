using System.Globalization;
using System.Text.RegularExpressions;

namespace TranscriptReconcile;

/// <summary>Parse a WebVTT transcript (Teams "Download transcript > VTT"). Each cue is a
/// timestamp range plus text tagged with the speaker as <c>&lt;v Display Name&gt;…&lt;/v&gt;</c>.
/// Times are seconds from the VTT's zero (meeting start), which is the common axis we use.</summary>
public static partial class VttParser
{
    public static IReadOnlyList<Utterance> Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var utterances = new List<Utterance>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) continue;

            var start = TryParseTimestamp(line[..arrow].Trim());
            var endText = line[(arrow + 3)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var end = endText.Length > 0 ? TryParseTimestamp(endText[0]) : null;
            if (start is null) continue;

            // Cue payload: the following lines up to a blank line.
            var speaker = "";
            var text = new List<string>();
            for (var k = i + 1; k < lines.Length && lines[k].Trim().Length > 0; k++)
            {
                var payload = lines[k];
                var voice = VoiceTag().Match(payload);
                if (voice.Success && speaker.Length == 0) speaker = voice.Groups[1].Value.Trim();
                var stripped = Tags().Replace(payload, "").Trim();
                if (stripped.Length > 0) text.Add(stripped);
                i = k;
            }

            var joined = string.Join(" ", text).Trim();
            if (joined.Length > 0)
            {
                utterances.Add(new Utterance(start.Value, end, speaker.Length > 0 ? speaker : "Unknown", joined));
            }
        }
        return utterances;
    }

    /// <summary>Parse "HH:MM:SS.mmm" or "MM:SS.mmm" (comma or dot for the fraction) to seconds.</summary>
    private static double? TryParseTimestamp(string token)
    {
        var t = token.Replace(',', '.');
        var parts = t.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        try
        {
            double h = 0, m, s;
            if (parts.Length == 3)
            {
                h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                s = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            else
            {
                m = double.Parse(parts[0], CultureInfo.InvariantCulture);
                s = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return h * 3600 + m * 60 + s;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<v\s+([^>]*)>")]
    private static partial Regex VoiceTag();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
