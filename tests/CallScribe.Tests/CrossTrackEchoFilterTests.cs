using CallScribe.Transcription;

namespace CallScribe.Tests;

public class CrossTrackEchoFilterTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    [Fact]
    public void MicEchoOfRecentOthersCaption_IsDetected()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "It has to actually listen to the output device.", T0);

        // Same words heard by the mic a moment later, slightly garbled.
        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "it has to actually listen to the output", T0.AddSeconds(2));

        Assert.True(isEcho);
    }

    [Fact]
    public void IndependentMicSpeech_IsNotAnEcho()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "The pocket clip slides right in here, nice and solid.", T0);

        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "Test, test, one, two, three, testing.", T0.AddSeconds(1));

        Assert.False(isEcho);
    }

    [Fact]
    public void ShortBackchannel_IsNeverSuppressed()
    {
        // "Yeah." on both tracks is normal conversation, not bleed.
        var filter = new CrossTrackEchoFilter();
        filter.Record("Others", "Yeah, okay.", T0);

        Assert.False(filter.IsEchoOfOtherTrack("Me", "Yeah, okay.", T0.AddSeconds(1)));
    }

    [Fact]
    public void OldCaptions_AgeOutOfTheWindow()
    {
        var filter = new CrossTrackEchoFilter(window: TimeSpan.FromSeconds(12));
        filter.Record("Others", "It has to actually listen to the output device.", T0);

        var isEcho = filter.IsEchoOfOtherTrack(
            "Me", "it has to actually listen to the output device", T0.AddSeconds(30));

        Assert.False(isEcho);
    }

    [Fact]
    public void SameTrack_NeverMatchesItself()
    {
        var filter = new CrossTrackEchoFilter();
        filter.Record("Me", "It has to actually listen to the output device.", T0);

        Assert.False(filter.IsEchoOfOtherTrack(
            "Me", "It has to actually listen to the output device.", T0.AddSeconds(1)));
    }
}
