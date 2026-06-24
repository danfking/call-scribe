using CallScribe.Coach.Llm;

namespace CallScribe.Tests;

public class OllamaChatTests
{
    [Theory]
    [InlineData("<think>weighing it up</think>The answer.", "The answer.")]
    [InlineData("reasoning with no opening tag\n</think>\n\nThe answer.", "The answer.")] // qwen3 think=false leak
    [InlineData("<think>multi\nline\nreasoning</think>\n\nFinal answer.", "Final answer.")]
    [InlineData("Just the answer, no thinking.", "Just the answer, no thinking.")]
    public void StripThink_KeepsOnlyTheAnswerAfterTheReasoning(string raw, string expected) =>
        Assert.Equal(expected, OllamaChat.StripThink(raw));
}
