using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LiveStatusDisplayBarTests
{
    [Theory]
    [InlineData(0, 100, 10, 0)]
    [InlineData(100, 100, 10, 10)]
    [InlineData(50, 100, 10, 5)]
    public void FilledCells_CountsTheObviousFractions(int value, int max, int width, int expected) =>
        Assert.Equal(expected, LiveStatusDisplay.FilledCells(value, max, width));

    [Fact]
    public void FilledCells_ShowsAtLeastOneCell_WhileAlive()
    {
        // 1/100 rounds to zero cells; "alive" must stay visibly alive.
        Assert.Equal(1, LiveStatusDisplay.FilledCells(1, 100, 10));
    }

    [Fact]
    public void FilledCells_NeverShowsFull_UntilActuallyFull()
    {
        // 99/100 rounds to a full bar; "almost done" must stay visibly unfinished.
        Assert.Equal(9, LiveStatusDisplay.FilledCells(99, 100, 10));
    }

    [Fact]
    public void FilledCells_ClampsOutOfRangeValues()
    {
        Assert.Equal(10, LiveStatusDisplay.FilledCells(150, 100, 10));
        Assert.Equal(0, LiveStatusDisplay.FilledCells(-5, 100, 10));
    }

    [Fact]
    public void FilledCells_GuardsDegenerateInputs()
    {
        Assert.Equal(0, LiveStatusDisplay.FilledCells(5, 0, 10));
        Assert.Equal(0, LiveStatusDisplay.FilledCells(5, 10, 0));
    }
}
