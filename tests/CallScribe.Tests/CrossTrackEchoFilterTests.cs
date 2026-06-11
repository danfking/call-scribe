using CallScribe.Transcription;

namespace CallScribe.Tests;

public class CrossTrackEchoFilterTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    [Fact]
    public void MicEchoWithOverlappingSpan_IsDetected()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "It has to actually listen to the output device.", T0, T0.AddSeconds(8));

        // Same words heard by the mic over a similar window, slightly garbled.
        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "it has to actually listen to the output", T0.AddSeconds(1), T0.AddSeconds(7));

        Assert.True(isEcho);
    }

    [Fact]
    public void SameWordsWithDisjointSpans_AreNotAnEcho()
    {
        // Someone genuinely repeating a phrase half a minute later is conversation.
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "It has to actually listen to the output device.", T0, T0.AddSeconds(8));

        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "it has to actually listen to the output device", T0.AddSeconds(30), T0.AddSeconds(36));

        Assert.False(isEcho);
    }

    [Fact]
    public void IndependentMicSpeech_IsNotAnEcho()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "The pocket clip slides right in here, nice and solid.", T0, T0.AddSeconds(8));

        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "Test, test, one, two, three, testing.", T0.AddSeconds(2), T0.AddSeconds(6));

        Assert.False(isEcho);
    }

    [Fact]
    public void ShortBackchannel_IsNeverSuppressed()
    {
        // "Yeah, okay." on both tracks at once is normal conversation, not bleed.
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "Yeah, okay.", T0, T0.AddSeconds(2));

        Assert.False(filter.IsEchoOfOtherTrack("Me", "Yeah, okay.", T0, T0.AddSeconds(2)));
    }

    [Fact]
    public void SpanSlack_CoversChunkBoundaryDifferences()
    {
        // Tracks chunk independently: the mic copy can start just after the
        // loopback span ended. Slack absorbs the boundary difference.
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "We've got the new crossfire reels here, give them a try.", T0, T0.AddSeconds(4));

        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "we've got the new crossfire reels here give them a try", T0.AddSeconds(5), T0.AddSeconds(9));

        Assert.True(isEcho);
    }

    [Fact]
    public void SameTrack_NeverMatchesItself()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Me", "It has to actually listen to the output device.", T0, T0.AddSeconds(8));

        Assert.False(filter.IsEchoOfOtherTrack(
            "Me", "It has to actually listen to the output device.", T0, T0.AddSeconds(8)));
    }
}
