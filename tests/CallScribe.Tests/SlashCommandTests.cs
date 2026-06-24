using CallScribe.Transcription;

namespace CallScribe.Tests;

public class SlashCommandTests
{
    // A registry mirroring the real dashboard's commands; handlers are no-ops (the pure functions
    // under test read only the metadata).
    private static readonly IReadOnlyList<SlashCommandSpec> Specs =
    [
        new("/assign-name", "\"<label>\" \"<name>\"", true, _ => { }, ["/rename"]),
        new("/speakers", "list far-side speakers", false, _ => { }, []),
        new("/help", "show commands", false, _ => { }, []),
        new("/stop", "finish", false, _ => { }, []),
    ];

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
    public void Match_ResolvesCanonicalNameAndAlias_CaseInsensitively()
    {
        Assert.Equal("/assign-name", SlashCommand.Match("/assign-name", Specs)?.Name);
        Assert.Equal("/assign-name", SlashCommand.Match("/RENAME", Specs)?.Name); // alias, case-insensitive
        Assert.Null(SlashCommand.Match("/nope", Specs));
    }

    [Fact]
    public void Complete_OnCommandWord_ReturnsMatchingNamesAndAliases()
    {
        var matches = SlashCommand.Complete("/s", Specs, []);
        Assert.Contains("/speakers", matches);
        Assert.Contains("/stop", matches);
        Assert.DoesNotContain("/help", matches);
    }

    [Fact]
    public void Complete_IncludesAliases()
    {
        var matches = SlashCommand.Complete("/r", Specs, []);
        Assert.Contains("/rename", matches);
    }

    [Fact]
    public void Complete_OnFirstArgOfLabelCommand_ReturnsMatchingLabels()
    {
        var matches = SlashCommand.Complete("/assign-name Sp", Specs, ["Speaker 1", "Speaker 2", "Dan"]);
        Assert.Equal(["Speaker 1", "Speaker 2"], matches);
    }

    [Fact]
    public void Complete_NonSlashInput_ReturnsNothing()
    {
        Assert.Empty(SlashCommand.Complete("hello", Specs, ["Speaker 1"]));
    }

    [Fact]
    public void ApplyCompletion_OnCommandWord_AppendsSpace()
    {
        Assert.Equal("/speakers ", SlashCommand.ApplyCompletion("/spe", "/speakers"));
    }

    [Fact]
    public void ApplyCompletion_OnLabelArg_QuotesWhenNeeded()
    {
        Assert.Equal("""/assign-name "Speaker 1" """, SlashCommand.ApplyCompletion("/assign-name Spe", "Speaker 1"));
    }

    [Fact]
    public void Highlight_ColoursKnownVerbCyan_UnknownRed()
    {
        Assert.Contains("[cyan]/speakers[/]", SlashCommand.Highlight("/speakers", Specs));     // complete + known
        Assert.Contains("[cyan]/s[/]", SlashCommand.Highlight("/s", Specs));                   // prefix of a command
        Assert.Contains("[red]/nope[/]", SlashCommand.Highlight("/nope ", Specs));             // complete + unknown
    }

    [Fact]
    public void Highlight_EscapesUserText()
    {
        // Square brackets in an argument must be escaped so they are not parsed as Spectre markup.
        var markup = SlashCommand.Highlight("/assign-name [x]", Specs);
        Assert.Contains("[[x]]", markup);
    }
}
