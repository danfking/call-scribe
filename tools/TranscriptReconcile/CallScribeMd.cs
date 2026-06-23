using System.Globalization;
using System.Text.RegularExpressions;

namespace TranscriptReconcile;

/// <summary>Parse a call-scribe merged transcript (.md as written by TranscriptMerger): YAML
/// frontmatter, then <c>**Speaker** [HH:mm:ss]</c> blocks each followed by one text line per
/// segment. This is the FINAL source: it carries the diarized speaker names, which the per-track
/// JSON does not. Times are seconds from the meeting start (from the "started:" frontmatter);
/// interior lines of a block share the block's start time (the .md only stamps block starts).</summary>
public static partial class CallScribeMd
{
    public static IReadOnlyList<Utterance> Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = FindStart(lines);

        var utterances = new List<Utterance>();
        var speaker = "";
        var startSec = 0.0;
        foreach (var line in lines)
        {
            var header = Header().Match(line);
            if (header.Success)
            {
                speaker = header.Groups["sp"].Value.Trim();
                startSec = StampSeconds(header.Groups["t"].Value, start);
                continue;
            }
            // Frontmatter and the title precede the first speaker block, so an empty speaker (no
            // header seen yet) already skips them; no content-based frontmatter filter is needed
            // (one would wrongly drop real lines like "yeah: so anyway").
            var text = line.Trim();
            if (text.Length == 0 || text == "---" || text.StartsWith('#') || speaker.Length == 0) continue;
            utterances.Add(new Utterance(startSec, null, speaker, text));
        }
        return utterances;
    }

    private static DateTime? FindStart(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("started:", StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(line["started:".Length..].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
            {
                return dt;
            }
        }
        return null;
    }

    /// <summary>Block stamp "HH:mm:ss" to seconds from the meeting start. When the start date is
    /// known the stamp is combined with it; otherwise the stamp's own time-of-day is used.</summary>
    private static double StampSeconds(string stamp, DateTime? start)
    {
        if (!TimeSpan.TryParseExact(stamp, [@"hh\:mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var tod))
        {
            return 0;
        }
        if (start is not { } s) return tod.TotalSeconds;
        var at = s.Date + tod;
        if (at < s) at = at.AddDays(1); // crossed midnight
        return (at - s).TotalSeconds;
    }

    [GeneratedRegex(@"^\*\*(?<sp>.+?)\*\*\s*\[(?<t>\d{1,2}:\d{2}:\d{2})\]\s*$")]
    private static partial Regex Header();
}
