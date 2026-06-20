using CallScribe.Coach;

namespace CallScribe.Tests;

public class AdviceFilterTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    [Fact]
    public void IdenticalAdvice_SecondIsSuppressed()
    {
        var filter = new AdviceFilter();
        Assert.True(filter.ShouldEmit("Priya prefers async written updates.", T0));
        Assert.False(filter.ShouldEmit("Priya prefers async written updates.", T0.AddSeconds(5)));
    }

    [Fact]
    public void HighOverlapParaphrase_IsSuppressed()
    {
        var filter = new AdviceFilter();
        Assert.True(filter.ShouldEmit("Priya prefers async written updates over status calls.", T0));
        // Same content, reordered/trimmed — high token overlap.
        Assert.False(filter.ShouldEmit("Status calls; Priya prefers async written updates.", T0.AddSeconds(10)));
    }

    [Fact]
    public void DistinctAdvice_IsEmitted()
    {
        var filter = new AdviceFilter();
        Assert.True(filter.ShouldEmit("They asked about the architecture — describe dual-track capture.", T0));
        Assert.True(filter.ShouldEmit("Confirm the Friday deadline for the latency benchmarks.", T0.AddSeconds(5)));
    }

    [Fact]
    public void SameAdvice_AfterRetentionWindow_IsEmittedAgain()
    {
        var filter = new AdviceFilter(retentionWindow: TimeSpan.FromSeconds(90));
        Assert.True(filter.ShouldEmit("Priya prefers async written updates.", T0));
        Assert.False(filter.ShouldEmit("Priya prefers async written updates.", T0.AddSeconds(30)));
        Assert.True(filter.ShouldEmit("Priya prefers async written updates.", T0.AddSeconds(120)));
    }

    [Fact]
    public void EmptyAdvice_IsNotEmitted()
    {
        var filter = new AdviceFilter();
        Assert.False(filter.ShouldEmit("   ", T0));
    }
}
