using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LiveStatusDisplayBarTests
{
    [Theory]
    [InlineData(0, 100, 10, "░░░░░░░░░░")]
    [InlineData(100, 100, 10, "██████████")]
    [InlineData(50, 100, 10, "█████░░░░░")]
    public void BlockBar_RendersTheObviousFractions(int value, int max, int width, string expected) =>
        Assert.Equal(expected, LiveStatusDisplay.BlockBar(value, max, width));

    [Fact]
    public void BlockBar_ShowsAtLeastOneCell_WhileAlive()
    {
        // 1/100 rounds to zero cells; "alive" must stay visibly alive.
        Assert.Equal("█░░░░░░░░░", LiveStatusDisplay.BlockBar(1, 100, 10));
    }

    [Fact]
    public void BlockBar_NeverShowsFull_UntilActuallyFull()
    {
        // 99/100 rounds to a full bar; "almost done" must stay visibly unfinished.
        Assert.Equal("█████████░", LiveStatusDisplay.BlockBar(99, 100, 10));
    }

    [Fact]
    public void BlockBar_ClampsOutOfRangeValues()
    {
        Assert.Equal("██████████", LiveStatusDisplay.BlockBar(150, 100, 10));
        Assert.Equal("░░░░░░░░░░", LiveStatusDisplay.BlockBar(-5, 100, 10));
    }

    [Fact]
    public void BlockBar_GuardsDegenerateInputs()
    {
        Assert.Equal("░░░░░░░░░░", LiveStatusDisplay.BlockBar(5, 0, 10));
        Assert.Equal("", LiveStatusDisplay.BlockBar(5, 10, 0));
    }
}
