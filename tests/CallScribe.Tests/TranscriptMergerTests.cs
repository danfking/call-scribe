using CallScribe.Transcription;

namespace CallScribe.Tests;

public class TranscriptMergerTests
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "call-scribe-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Merge_InterleavesChronologicallyWithSpeakerGrouping()
    {
        var others = new TrackTranscript("Others", 30, [
            new TranscriptSegment(5.0, 8.0, "Hello from the other side."),
            new TranscriptSegment(20.0, 24.0, "Sounds good."),
        ]);
        var me = new TrackTranscript("Me", 30, [
            new TranscriptSegment(0.0, 4.0, "Hi, can you hear me?"),
            new TranscriptSegment(10.0, 14.0, "Great, let's start."),
            new TranscriptSegment(15.0, 18.0, "First item is the report."),
        ]);

        var path = TranscriptMerger.Merge("2026-06-11-1400-standup", others, me, TempDir);
        var content = File.ReadAllText(path);

        // Wall-clock stamps derived from the stem (14:00) plus segment offsets.
        Assert.Contains("**Me** [14:00:00]", content);
        Assert.Contains("**Others** [14:00:05]", content);
        Assert.Contains("started: 2026-06-11 14:00", content);
        Assert.Contains("label: standup", content);

        // Consecutive Me segments (10s and 15s) share one speaker header.
        var meHeaders = content.Split("**Me**").Length - 1;
        Assert.Equal(2, meHeaders);

        // Chronological order: Me(0) < Others(5) < Me(10).
        var first = content.IndexOf("Hi, can you hear me?");
        var second = content.IndexOf("Hello from the other side.");
        var third = content.IndexOf("Great, let's start.");
        Assert.True(first < second && second < third);
    }

    [Fact]
    public void MergeLive_WritesFinalFormat_GroupingConsecutiveSpeakers()
    {
        var t0 = new DateTime(2026, 6, 11, 15, 0, 0, DateTimeKind.Local);
        var lines = new List<(DateTime At, string Speaker, string Text)>
        {
            (t0, "Me", "Morning everyone."),
            (t0.AddSeconds(5), "Kiel", "Quick update from me."),
            (t0.AddSeconds(9), "Kiel", "Shipped the fix yesterday."), // consecutive: shares the header
            (t0.AddSeconds(15), "Me", "Thanks Kiel."),
        };

        var path = TranscriptMerger.MergeLive("2026-06-11-1500-standup", lines, TempDir);
        var content = File.ReadAllText(path);

        Assert.Contains("source: live", content);              // marked as the live transcript
        Assert.Contains("started: 2026-06-11 15:00", content);
        Assert.Contains("label: standup", content);
        Assert.Contains("**Me** [15:00:00]", content);
        Assert.Contains("**Kiel** [15:00:05]", content);
        Assert.Equal(1, content.Split("**Kiel**").Length - 1); // two consecutive Kiel lines, one header
        Assert.Equal(2, content.Split("**Me**").Length - 1);   // Me opens and resumes after Kiel

        var firstKiel = content.IndexOf("Quick update");
        var secondKiel = content.IndexOf("Shipped the fix");
        var backToMe = content.IndexOf("Thanks Kiel");
        Assert.True(firstKiel < secondKiel && secondKiel < backToMe);
    }

    [Fact]
    public void Merge_FallsBackToElapsedTimestampsForNonStandardStem()
    {
        var others = new TrackTranscript("Others", 10, [new TranscriptSegment(2.0, 4.0, "Test.")]);
        var me = new TrackTranscript("Me", 10, []);

        var path = TranscriptMerger.Merge("custom-name", others, me, TempDir);
        var content = File.ReadAllText(path);

        Assert.Contains("**Others** [00:02]", content);
    }

    [Fact]
    public void Merge_AppliesPerSegmentOthersSpeakerNames()
    {
        var others = new TrackTranscript("Others", 30, [
            new TranscriptSegment(5.0, 8.0, "Price question here."),
            new TranscriptSegment(20.0, 24.0, "Different person now."),
        ]);
        var me = new TrackTranscript("Me", 30, []);

        // Name far-side segments by start time, as after-meeting diarization would.
        var path = TranscriptMerger.Merge("2026-06-11-1400", others, me, TempDir,
            s => s.Start < 10 ? "Gavin" : "Priya");
        var content = File.ReadAllText(path);

        Assert.Contains("**Gavin** [14:00:05]", content);
        Assert.Contains("**Priya** [14:00:20]", content);
        Assert.DoesNotContain("**Others**", content);
    }

    [Fact]
    public void Merge_UsesMeSpeakerLabelWhenGiven()
    {
        var others = new TrackTranscript("Others", 10, []);
        var me = new TrackTranscript("Me", 10, [new TranscriptSegment(1.0, 2.0, "My line.")]);

        var path = TranscriptMerger.Merge("2026-06-11-1400", others, me, TempDir, othersSpeaker: null, meSpeaker: "Dan");
        var content = File.ReadAllText(path);

        Assert.Contains("**Dan**", content);
        Assert.DoesNotContain("**Me**", content);
    }

    [Fact]
    public void MergeLive_FromALiveCaptionLog_ProducesTheSameArtifact()
    {
        // The live-first stop path (start and record stop) reads {stem}.live.jsonl back and
        // feeds it straight to MergeLive; this pins that composed contract.
        var t0 = new DateTime(2026, 6, 11, 15, 0, 0, DateTimeKind.Local);
        var logPath = Path.Combine(TempDir, "roundtrip.live.jsonl");
        using (var log = LiveCaptionLog.TryCreate(logPath))
        {
            log!.Append(new CaptionEvent(t0, "Me", "Morning everyone."));
            log.Append(new CaptionEvent(t0.AddSeconds(5), "Others", "Quick update from me.", Speaker: "Kiel"));
            log.Append(new CaptionEvent(t0.AddSeconds(9), "Others", "Shipped the fix yesterday.", Speaker: "Kiel"));
            log.Append(new CaptionEvent(t0.AddSeconds(15), "Me", "Thanks Kiel."));
        }

        var fromLog = TranscriptMerger.MergeLive(
            "2026-06-11-1500-standup", LiveCaptionLog.Read(logPath), Path.Combine(TempDir, "from-log"));
        var fromTuples = TranscriptMerger.MergeLive(
            "2026-06-11-1500-standup",
            [
                (t0, "Me", "Morning everyone."),
                (t0.AddSeconds(5), "Kiel", "Quick update from me."),
                (t0.AddSeconds(9), "Kiel", "Shipped the fix yesterday."),
                (t0.AddSeconds(15), "Me", "Thanks Kiel."),
            ],
            Path.Combine(TempDir, "from-tuples"));

        Assert.Equal(File.ReadAllText(fromTuples), File.ReadAllText(fromLog));
        Assert.Contains("source: live", File.ReadAllText(fromLog));
    }

    [Fact]
    public void Merge_SkipsEmptySegments()
    {
        var others = new TrackTranscript("Others", 10, [new TranscriptSegment(1.0, 2.0, "  ")]);
        var me = new TrackTranscript("Me", 10, [new TranscriptSegment(3.0, 4.0, "Real text.")]);

        var path = TranscriptMerger.Merge("2026-06-11-1500", others, me, TempDir);
        var content = File.ReadAllText(path);

        Assert.DoesNotContain("**Others**", content);
        Assert.Contains("Real text.", content);
    }
}
