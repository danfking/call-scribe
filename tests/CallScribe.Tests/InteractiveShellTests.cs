using CallScribe.Commands;

namespace CallScribe.Tests;

public class InteractiveShellTests
{
    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        Assert.Equal(["config", "set", "liveModel", "base.en"],
            InteractiveShell.Tokenize("config set liveModel base.en"));
    }

    [Fact]
    public void Tokenize_KeepsQuotedSpansTogether()
    {
        Assert.Equal(["coach", "enroll", "Bob Smith", "C:\\clips\\bob.wav"],
            InteractiveShell.Tokenize("coach enroll \"Bob Smith\" \"C:\\clips\\bob.wav\""));
    }

    [Fact]
    public void Tokenize_StripsLeadingCallScribe()
    {
        Assert.Equal(["devices"], InteractiveShell.Tokenize("call-scribe devices"));
    }

    [Fact]
    public void Tokenize_CollapsesRunsOfWhitespace()
    {
        Assert.Equal(["start", "--full"], InteractiveShell.Tokenize("  start    --full  "));
    }

    [Fact]
    public void Tokenize_EmptyLineYieldsNoTokens()
    {
        Assert.Empty(InteractiveShell.Tokenize("   "));
    }
}
