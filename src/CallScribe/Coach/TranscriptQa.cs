namespace CallScribe.Coach;

/// <summary>Prompt construction for the live <c>/ask</c> command: answer a question grounded only in
/// the meeting transcript so far. Kept pure (no LLM client) so the prompt contract is unit-testable;
/// the caller runs it through the chat model.</summary>
public static class TranscriptQa
{
    public const string SystemPrompt =
        "You answer a question about a live meeting transcript. Use ONLY the transcript provided; if "
        + "the answer is not in it, say you do not know. Be concise: 1 to 3 sentences, no preamble.";

    public static string BuildUserPrompt(string question, string transcript) =>
        $"Transcript so far:\n{transcript}\n\nQuestion: {question}";
}
