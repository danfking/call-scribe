using CallScribe.Transcription;

namespace CallScribe.Coach;

/// <summary>The Reflect+Plan+Act stages condensed into one decision: given the recent
/// transcript and the latest utterance, decide whether to surface advice and produce
/// it. Phase 1 ships a deterministic stub; later phases back this with local models
/// (fast model to triage, reasoning model to synthesise) behind the same interface.</summary>
public interface IAdvisor
{
    Task<AdviceEvent?> ConsiderAsync(
        IReadOnlyList<CaptionEvent> context, CaptionEvent latest, CancellationToken ct);
}

/// <summary>Deterministic placeholder advisor: surfaces a prompt when the other side
/// asks a question. No model calls, so the whole ORPA pipeline — caption seam, async
/// loop, advice panel — can be verified offline and in unit tests.</summary>
public sealed class StubAdvisor : IAdvisor
{
    public Task<AdviceEvent?> ConsiderAsync(
        IReadOnlyList<CaptionEvent> context, CaptionEvent latest, CancellationToken ct)
    {
        AdviceEvent? advice = null;
        if (latest.Label == LiveCaptionEngine.OthersLabel && latest.Caption.TrimEnd().EndsWith('?'))
        {
            advice = new AdviceEvent(
                DateTime.Now, AdviceKind.Answer,
                $"They asked a question — be ready to answer: \"{latest.Caption.Trim()}\"",
                "stub");
        }
        return Task.FromResult(advice);
    }
}
