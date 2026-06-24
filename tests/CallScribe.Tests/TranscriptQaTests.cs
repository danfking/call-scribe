using CallScribe.Coach;

namespace CallScribe.Tests;

public class TranscriptQaTests
{
    [Fact]
    public void BuildUserPrompt_IncludesTranscriptAndQuestion()
    {
        var prompt = TranscriptQa.BuildUserPrompt(
            "What did Kiel commit to?",
            "Kiel: I'll ship the fix today.\nDan: thanks.");

        Assert.Contains("Kiel: I'll ship the fix today.", prompt);
        Assert.Contains("What did Kiel commit to?", prompt);
    }

    [Fact]
    public void SystemPrompt_ConstrainsToTheTranscript()
    {
        // The contract that keeps answers grounded: only the transcript, admit when unknown.
        Assert.Contains("ONLY the transcript", TranscriptQa.SystemPrompt);
        Assert.Contains("do not know", TranscriptQa.SystemPrompt);
    }
}
