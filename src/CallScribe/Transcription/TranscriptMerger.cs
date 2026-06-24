using System.Globalization;
using System.Text;

namespace CallScribe.Transcription;

/// <summary>Interleaves the Me and Others track transcripts into one chronological
/// markdown document. Direct port of the prototype's merge.py: same frontmatter,
/// same speaker grouping, same wall-clock timestamps derived from the filename stem.</summary>
public static class TranscriptMerger
{
    /// <param name="othersSpeaker">Optional per-segment naming for the Others track (from
    /// after-meeting diarization); when null every far-side segment is labelled "Others".</param>
    /// <param name="meSpeaker">Label for the near (mic) track; defaults to "Me", overridden
    /// with the user's name once their voice is enrolled.</param>
    public static string Merge(
        string stem, TrackTranscript others, TrackTranscript me, string outputDir,
        Func<TranscriptSegment, string>? othersSpeaker = null, string meSpeaker = "Me")
    {
        var start = ParseStart(stem);
        var lines = others.Segments.Select(s => (Speaker: othersSpeaker?.Invoke(s) ?? "Others", Segment: s))
            .Concat(me.Segments.Select(s => (Speaker: meSpeaker, Segment: s)))
            .OrderBy(x => x.Segment.Start)
            .Select(x => (x.Speaker, x.Segment.Text, Stamp: StampOffset(start, x.Segment.Start)))
            .ToList();

        var duration = TimeSpan.FromSeconds(Math.Max(others.Duration, me.Duration));
        return Write(stem, outputDir, duration, source: null, lines);
    }

    /// <summary>Write the live transcript (the per-chunk captions persisted to the coach DB during
    /// the meeting, already carrying resolved/consolidated speaker names) as a .md in the SAME format
    /// as <see cref="Merge"/>, so the live-only path produces the same artifact the batch pass would.
    /// A <c>source: live</c> frontmatter line marks it as the lower-latency, slightly-lower-accuracy
    /// transcript rather than the full-quality final one. Each line already knows its wall-clock time
    /// and speaker, so this just groups consecutive same-speaker lines under a stamped header.</summary>
    /// <param name="duration">The recorded meeting length for the frontmatter. When null it is
    /// approximated by the span between the first and last caption, which omits leading/trailing
    /// silence; pass the real capture duration when it is known (the listen flow has it).</param>
    public static string MergeLive(
        string stem, IReadOnlyList<(DateTime At, string Speaker, string Text)> lines, string outputDir,
        TimeSpan? duration = null)
    {
        var ordered = lines.OrderBy(l => l.At).ToList();
        var elapsed = duration ?? (ordered.Count > 0
            ? TimeSpan.FromSeconds(Math.Max(0, (ordered[^1].At - ordered[0].At).TotalSeconds))
            : TimeSpan.Zero);

        var stamped = ordered
            .Select(l => (l.Speaker, l.Text, Stamp: l.At.ToLocalTime().ToString("HH:mm:ss")))
            .ToList();
        return Write(stem, outputDir, elapsed, source: "live", stamped);
    }

    /// <summary>Shared writer for both transcripts: the YAML frontmatter block plus the speaker-grouped
    /// body, where consecutive same-speaker lines share one stamped header. Each line arrives already
    /// stamped because the batch path stamps from per-segment offsets and the live path from wall-clock
    /// times. <paramref name="source"/>, when set, adds a frontmatter marker (e.g. "live").</summary>
    private static string Write(
        string stem, string outputDir, TimeSpan duration, string? source,
        IReadOnlyList<(string Speaker, string Text, string Stamp)> lines)
    {
        Directory.CreateDirectory(outputDir);
        var start = ParseStart(stem);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"started: {(start is { } s0 ? s0.ToString("yyyy-MM-dd HH:mm") : stem.Length >= 10 ? stem[..10] : stem)}");
        sb.AppendLine($"label: {(stem.Length > 16 ? stem[16..] : "")}");
        sb.AppendLine($"duration: {FormatElapsed(duration)}");
        sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-dd}");
        if (source != null) sb.AppendLine($"source: {source}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Call transcript: {stem}");
        sb.AppendLine();

        string? currentSpeaker = null;
        foreach (var (speaker, text, stamp) in lines)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (speaker != currentSpeaker)
            {
                sb.AppendLine();
                sb.AppendLine($"**{speaker}** [{stamp}]");
                currentSpeaker = speaker;
            }
            sb.AppendLine(text);
        }

        var outputPath = Path.Combine(outputDir, $"{stem}.md");
        File.WriteAllText(outputPath, sb.ToString());
        return outputPath;
    }

    /// <summary>Wall-clock stamp for a segment at <paramref name="offsetSeconds"/> into the call:
    /// the meeting start plus the offset, or just the elapsed offset when the stem has no start date.</summary>
    private static string StampOffset(DateTime? start, double offsetSeconds)
    {
        var offset = TimeSpan.FromSeconds(offsetSeconds);
        return start is { } s ? (s + offset).ToString("HH:mm:ss") : FormatElapsed(offset);
    }

    /// <summary>Recording stems start with yyyy-MM-dd-HHmm, written at call start.</summary>
    private static DateTime? ParseStart(string stem) =>
        stem.Length >= 15 && DateTime.TryParseExact(
            stem[..15], "yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string FormatElapsed(TimeSpan t) =>
        t.Hours > 0 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
}
