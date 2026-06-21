using CallScribe.Coach;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class CoachEngineTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    /// <summary>Advisor that records what it saw and emits one canned advice per call,
    /// so the test can assert the loop drains captions in order and forwards advice.</summary>
    private sealed class RecordingAdvisor : IAdvisor
    {
        public readonly List<string> Seen = [];

        public Task<AdviceEvent?> ConsiderAsync(
            IReadOnlyList<CaptionEvent> context, CaptionEvent latest, CancellationToken ct)
        {
            Seen.Add(latest.Caption);
            AdviceEvent? advice = new AdviceEvent(latest.At, AdviceKind.Tip, $"saw: {latest.Caption}", "test");
            return Task.FromResult(advice);
        }
    }

    [Fact]
    public async Task ObservedCaptions_AreProcessedInOrder_AndAdviceIsEmitted()
    {
        var advisor = new RecordingAdvisor();
        var emitted = new List<AdviceEvent>();
        using var coach = new CoachEngine(advisor);
        coach.AdviceEmitted += emitted.Add;

        coach.Observe(new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "first"));
        coach.Observe(new CaptionEvent(T0.AddSeconds(2), LiveCaptionEngine.MeLabel, "second"));
        await coach.CompleteAsync();

        Assert.Equal(["first", "second"], advisor.Seen);
        Assert.Equal(["saw: first", "saw: second"], emitted.Select(a => a.Text));
    }

    /// <summary>Always returns the same advice text, to exercise de-duplication.</summary>
    private sealed class FixedAdvisor(string text) : IAdvisor
    {
        public Task<AdviceEvent?> ConsiderAsync(
            IReadOnlyList<CaptionEvent> context, CaptionEvent latest, CancellationToken ct) =>
            Task.FromResult<AdviceEvent?>(new AdviceEvent(DateTime.Now, AdviceKind.Tip, text, "test"));
    }

    [Fact]
    public async Task RepeatedAdvice_IsEmittedOnlyOnce()
    {
        var emitted = new List<AdviceEvent>();
        using var coach = new CoachEngine(new FixedAdvisor("Priya prefers async written updates."));
        coach.AdviceEmitted += emitted.Add;

        coach.Observe(new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "first line"));
        coach.Observe(new CaptionEvent(T0.AddSeconds(2), LiveCaptionEngine.OthersLabel, "second line"));
        coach.Observe(new CaptionEvent(T0.AddSeconds(4), LiveCaptionEngine.OthersLabel, "third line"));
        await coach.CompleteAsync();

        Assert.Single(emitted);
    }

    [Fact]
    public async Task StubAdvisor_AdvisesOnOthersQuestion_Only()
    {
        var emitted = new List<AdviceEvent>();
        using var coach = new CoachEngine(new StubAdvisor());
        coach.AdviceEmitted += emitted.Add;

        coach.Observe(new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "Can you explain the design?"));
        coach.Observe(new CaptionEvent(T0.AddSeconds(3), LiveCaptionEngine.OthersLabel, "Thanks, that helps."));
        coach.Observe(new CaptionEvent(T0.AddSeconds(6), LiveCaptionEngine.MeLabel, "Anything else?"));
        await coach.CompleteAsync();

        var advice = Assert.Single(emitted);
        Assert.Equal(AdviceKind.Answer, advice.Kind);
        Assert.Contains("Can you explain the design?", advice.Text);
    }
}
