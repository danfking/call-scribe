using CallScribe.Transcription;

namespace CallScribe.Tests;

public class SlashCommandTests
{
    [Fact]
    public void ParseCommandLine_KeepsQuotedArgsTogether()
    {
        var (cmd, args) = SlashCommand.ParseCommandLine("""/assign-name "Speaker 1" "Sammy" """);

        Assert.Equal("/assign-name", cmd);
        Assert.Equal(["Speaker 1", "Sammy"], args);
    }

    [Theory]
    [InlineData("/speakers", "/speakers")]
    [InlineData("/stop", "/stop")]
    public void ParseCommandLine_HandlesBareCommands(string line, string expected)
    {
        var (cmd, args) = SlashCommand.ParseCommandLine(line);
        Assert.Equal(expected, cmd);
        Assert.Empty(args);
    }

    [Fact]
    public void ParseCommandLine_EmptyLine_YieldsNoCommand()
    {
        var (cmd, args) = SlashCommand.ParseCommandLine("   ");
        Assert.Equal("", cmd);
        Assert.Empty(args);
    }

    [Fact]
    public void Complete_OnCommandWord_ReturnsMatchingCommands()
    {
        var matches = SlashCommand.Complete("/s", []);
        Assert.Contains("/speakers", matches);
        Assert.Contains("/stop", matches);
        Assert.DoesNotContain("/help", matches);
    }

    [Fact]
    public void Complete_OnFirstArgOfAssign_ReturnsMatchingLabels()
    {
        var matches = SlashCommand.Complete("/assign-name Sp", ["Speaker 1", "Speaker 2", "Dan"]);
        Assert.Equal(["Speaker 1", "Speaker 2"], matches);
    }

    [Fact]
    public void Complete_NonSlashInput_ReturnsNothing()
    {
        Assert.Empty(SlashCommand.Complete("hello", ["Speaker 1"]));
    }

    [Fact]
    public void ApplyTab_CompletesUniqueCommand()
    {
        Assert.Equal("/speakers ", SlashCommand.ApplyTab("/spe", []));
    }

    [Fact]
    public void ApplyTab_CompletesUniqueLabel_WithQuotingWhenNeeded()
    {
        Assert.Equal("""/assign-name "Speaker 1" """, SlashCommand.ApplyTab("/assign-name Spe", ["Speaker 1"]));
    }

    [Fact]
    public void ApplyTab_LeavesAmbiguousInputUnchanged()
    {
        Assert.Equal("/s", SlashCommand.ApplyTab("/s", [])); // /speakers and /stop both match
    }
}
