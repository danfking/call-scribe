using CallScribe.Coach.Speaker;

namespace CallScribe.Tests;

public class SpeakerNameExtractorTests
{
    [Theory]
    [InlineData("I'm Sammy here, thanks for joining me.", "Sammy")]
    [InlineData("this is Priya from the platform team.", "Priya")]
    [InlineData("My name is Bob and I'll run the demo.", "Bob")]
    [InlineData("Sammy here. Let's get started.", "Sammy")]
    [InlineData("I am Tariq, good to meet you.", "Tariq")]
    public void DetectRegex_ExtractsSelfIntroducedName(string text, string expected)
    {
        var found = SpeakerNameExtractor.DetectRegex([("Speaker 1", text)]);
        Assert.Equal(expected, found.GetValueOrDefault("Speaker 1"));
    }

    [Theory]
    [InlineData("I'm done with that section.")]
    [InlineData("I'm not sure about the pricing.")]
    [InlineData("This is great, thanks.")]
    [InlineData("Okay, let's move on.")]
    [InlineData("I'm going to share my screen.")]
    public void DetectRegex_IgnoresNonIntroductions(string text)
    {
        var found = SpeakerNameExtractor.DetectRegex([("Speaker 1", text)]);
        Assert.False(found.ContainsKey("Speaker 1"));
    }

    [Fact]
    public void DetectRegex_KeepsFirstNamePerSpeaker_AndMapsBySpeaker()
    {
        var found = SpeakerNameExtractor.DetectRegex(
        [
            ("Speaker 1", "Hey, I'm Sammy."),
            ("Speaker 2", "And this is Priya."),
            ("Speaker 1", "Actually call me Sam."), // not an intro pattern; first stands
        ]);

        Assert.Equal("Sammy", found["Speaker 1"]);
        Assert.Equal("Priya", found["Speaker 2"]);
    }

    [Fact]
    public void ParseNameMap_ReadsWellFormedJson()
    {
        const string json = """{"names":[{"speaker":"Speaker 1","name":"Sammy"},{"speaker":"Speaker 2","name":"Priya"}]}""";
        var map = SpeakerNameExtractor.ParseNameMap(json);

        Assert.Equal("Sammy", map["Speaker 1"]);
        Assert.Equal("Priya", map["Speaker 2"]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"names":[{"speaker":"Speaker 1"}]}""")]   // missing name -> skipped
    [InlineData("""{"names":[]}""")]
    public void ParseNameMap_IsTolerantOfBadOrEmptyOutput(string json)
    {
        Assert.Empty(SpeakerNameExtractor.ParseNameMap(json));
    }
}
