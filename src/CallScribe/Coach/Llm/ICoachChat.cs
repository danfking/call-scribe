namespace CallScribe.Coach.Llm;

/// <summary>Minimal chat seam the coach talks to. One implementation today (local
/// Ollama); the same interface lets a Claude-API client (or any other provider) drop
/// in later as the escalation tier — the advisor code doesn't change.</summary>
public interface ICoachChat
{
    /// <summary>Single-shot completion. <paramref name="jsonMode"/> asks the provider to
    /// constrain output to valid JSON (used for the structured advise/skip decision).
    /// <paramref name="maxTokens"/> caps the reply length — small for a one-line advice,
    /// large for end-of-meeting consolidation whose JSON array can be long.</summary>
    Task<string> CompleteAsync(
        string model, string system, string user, bool jsonMode, int maxTokens, CancellationToken ct);
}
