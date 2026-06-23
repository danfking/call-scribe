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
        Directory.CreateDirectory(outputDir);

        var merged = others.Segments.Select(s => (Speaker: othersSpeaker?.Invoke(s) ?? "Others", Segment: s))
            .Concat(me.Segments.Select(s => (Speaker: meSpeaker, Segment: s)))
            .OrderBy(x => x.Segment.Start)
            .ToList();

        var duration = TimeSpan.FromSeconds(Math.Max(others.Duration, me.Duration));
        var start = ParseStart(stem);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"started: {(start is { } s0 ? s0.ToString("yyyy-MM-dd HH:mm") : stem.Length >= 10 ? stem[..10] : stem)}");
        sb.AppendLine($"label: {(stem.Length > 16 ? stem[16..] : "")}");
        sb.AppendLine($"duration: {FormatElapsed(duration)}");
        sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Call transcript: {stem}");
        sb.AppendLine();

        string? currentSpeaker = null;
        foreach (var (speaker, segment) in merged)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;
            if (speaker != currentSpeaker)
            {
                var offset = TimeSpan.FromSeconds(segment.Start);
                var stamp = start is { } s
                    ? (s + offset).ToString("HH:mm:ss")
                    : FormatElapsed(offset);
                sb.AppendLine();
                sb.AppendLine($"**{speaker}** [{stamp}]");
                currentSpeaker = speaker;
            }
            sb.AppendLine(segment.Text);
        }

        var outputPath = Path.Combine(outputDir, $"{stem}.md");
        File.WriteAllText(outputPath, sb.ToString());
        return outputPath;
    }

    /// <summary>Write the live transcript (the per-chunk captions persisted to the coach DB during
    /// the meeting, already carrying resolved/consolidated speaker names) as a .md in the SAME format
    /// as <see cref="Merge"/>, so the live-only path produces the same artifact the batch pass would.
    /// A <c>source: live</c> frontmatter line marks it as the lower-latency, slightly-lower-accuracy
    /// transcript rather than the full-quality final one. Each line already knows its wall-clock time
    /// and speaker, so this just groups consecutive same-speaker lines under a stamped header.</summary>
    public static string MergeLive(
        string stem, IReadOnlyList<(DateTime At, string Speaker, string Text)> lines, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var ordered = lines.OrderBy(l => l.At).ToList();
        var start = ParseStart(stem);
        var duration = ordered.Count > 0
            ? TimeSpan.FromSeconds(Math.Max(0, (ordered[^1].At - ordered[0].At).TotalSeconds))
            : TimeSpan.Zero;

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"started: {(start is { } s0 ? s0.ToString("yyyy-MM-dd HH:mm") : stem.Length >= 10 ? stem[..10] : stem)}");
        sb.AppendLine($"label: {(stem.Length > 16 ? stem[16..] : "")}");
        sb.AppendLine($"duration: {FormatElapsed(duration)}");
        sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine("source: live"); // distinguishes from the full-quality batch ("final") transcript
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Call transcript: {stem}");
        sb.AppendLine();

        string? currentSpeaker = null;
        foreach (var line in ordered)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (line.Speaker != currentSpeaker)
            {
                sb.AppendLine();
                sb.AppendLine($"**{line.Speaker}** [{line.At.ToLocalTime():HH:mm:ss}]");
                currentSpeaker = line.Speaker;
            }
            sb.AppendLine(line.Text);
        }

        var outputPath = Path.Combine(outputDir, $"{stem}.md");
        File.WriteAllText(outputPath, sb.ToString());
        return outputPath;
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
