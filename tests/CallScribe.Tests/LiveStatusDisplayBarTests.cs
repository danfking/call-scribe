using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LiveStatusDisplayBarTests
{
    [Theory]
    [InlineData(0, 100, 10, "----------")]
    [InlineData(100, 100, 10, "==========")]
    [InlineData(50, 100, 10, "=====-----")]
    public void AsciiBar_RendersTheObviousFractions(int value, int max, int width, string expected) =>
        Assert.Equal(expected, LiveStatusDisplay.AsciiBar(value, max, width));

    [Fact]
    public void AsciiBar_ShowsAtLeastOneSegment_WhileAlive()
    {
        // 1/100 rounds to zero segments; "alive" must stay visibly alive.
        Assert.Equal("=---------", LiveStatusDisplay.AsciiBar(1, 100, 10));
    }

    [Fact]
    public void AsciiBar_NeverShowsFull_UntilActuallyFull()
    {
        // 99/100 rounds to a full bar; "almost done" must stay visibly unfinished.
        Assert.Equal("=========-", LiveStatusDisplay.AsciiBar(99, 100, 10));
    }

    [Fact]
    public void AsciiBar_ClampsOutOfRangeValues()
    {
        Assert.Equal("==========", LiveStatusDisplay.AsciiBar(150, 100, 10));
        Assert.Equal("----------", LiveStatusDisplay.AsciiBar(-5, 100, 10));
    }

    [Fact]
    public void AsciiBar_GuardsDegenerateInputs()
    {
        Assert.Equal("----------", LiveStatusDisplay.AsciiBar(5, 0, 10));
        Assert.Equal("", LiveStatusDisplay.AsciiBar(5, 10, 0));
    }
}
