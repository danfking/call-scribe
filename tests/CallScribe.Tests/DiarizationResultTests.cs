using CallScribe.Coach.Speaker;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class DiarizationResultTests
{
    private static DiarizationResult Build() => new(
        segments:
        [
            new DiarizedSegment(0.0, 5.0, 0),   // cluster 0
            new DiarizedSegment(5.0, 10.0, 1),  // cluster 1
            new DiarizedSegment(10.0, 15.0, 0), // cluster 0 again
        ],
        clusters:
        [
            new SpeakerCluster(0, "Gavin", Enrolled: true, MeanEmbedding: [1f, 0f]),
            new SpeakerCluster(1, "Speaker 1", Enrolled: false, MeanEmbedding: [0f, 1f]),
        ]);

    [Fact]
    public void SpeakerFor_PicksClusterWithGreatestOverlap()
    {
        var result = Build();

        Assert.Equal("Gavin", result.SpeakerFor(new TranscriptSegment(1.0, 4.0, "in cluster 0")));
        Assert.Equal("Speaker 1", result.SpeakerFor(new TranscriptSegment(6.0, 9.0, "in cluster 1")));
        // Straddles the boundary but leans into cluster 1 (4s in [5,10] vs 1s in [10,15]).
        Assert.Equal("Speaker 1", result.SpeakerFor(new TranscriptSegment(6.0, 11.0, "mostly 1")));
        Assert.Equal("Gavin", result.SpeakerFor(new TranscriptSegment(11.0, 14.0, "back in cluster 0")));
    }

    [Fact]
    public void SpeakerFor_FallsBackWhenNoOverlap()
    {
        var result = Build();

        Assert.Equal(LiveCaptionEngine.OthersLabel, result.SpeakerFor(new TranscriptSegment(100.0, 105.0, "off the end")));
    }

    [Fact]
    public void Rename_UpdatesSubsequentLookups()
    {
        var result = Build();
        result.Rename(1, "Priya");

        Assert.Equal("Priya", result.SpeakerFor(new TranscriptSegment(6.0, 9.0, "now named")));
    }
}
