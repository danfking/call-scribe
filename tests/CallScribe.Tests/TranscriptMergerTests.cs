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
    public void Merge_FallsBackToElapsedTimestampsForNonStandardStem()
    {
        var others = new TrackTranscript("Others", 10, [new TranscriptSegment(2.0, 4.0, "Test.")]);
        var me = new TrackTranscript("Me", 10, []);

        var path = TranscriptMerger.Merge("custom-name", others, me, TempDir);
        var content = File.ReadAllText(path);

        Assert.Contains("**Others** [00:02]", content);
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
