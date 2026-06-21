using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LlmAdvisorTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    /// <summary>Returns a fixed canned reply, standing in for the model so the advisor's
    /// JSON parsing and gating are tested deterministically without Ollama.</summary>
    private sealed class CannedChat(string reply) : ICoachChat
    {
        public string? LastUser { get; private set; }

        public Task<string> CompleteAsync(string model, string system, string user, bool jsonMode, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(reply);
        }
    }

    private static IReadOnlyList<CaptionEvent> Context() =>
        [new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "Can you walk me through the architecture?")];

    private static IReadOnlyList<CaptionEvent> MultiLineContext() =>
    [
        new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "Can you walk me through the architecture?"),
        new CaptionEvent(T0.AddSeconds(5), LiveCaptionEngine.MeLabel, "Sure, it's dual-track capture."),
        new CaptionEvent(T0.AddSeconds(10), LiveCaptionEngine.OthersLabel, "How do you handle latency?"),
    ];

    [Fact]
    public async Task AdviseTrue_ProducesAdviceWithMappedKind()
    {
        var chat = new CannedChat("""{"advise": true, "kind": "answer", "advice": "Describe the dual-track capture."}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b");

        var advice = await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.NotNull(advice);
        Assert.Equal(AdviceKind.Answer, advice!.Value.Kind);
        Assert.Equal("Describe the dual-track capture.", advice.Value.Text);
        Assert.Contains("Recent transcript", chat.LastUser);
    }

    [Fact]
    public async Task AdviseFalse_StaysSilent()
    {
        var chat = new CannedChat("""{"advise": false, "kind": "tip", "advice": ""}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b");

        var advice = await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.Null(advice);
    }

    [Fact]
    public async Task MalformedJson_StaysSilent()
    {
        var chat = new CannedChat("not json at all");
        var advisor = new LlmAdvisor(chat, "qwen3:4b");

        var advice = await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.Null(advice);
    }

    [Fact]
    public async Task AdviseTrueButEmptyAdvice_StaysSilent()
    {
        var chat = new CannedChat("""{"advise": true, "kind": "tip", "advice": "   "}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b");

        var advice = await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.Null(advice);
    }

    /// <summary>Returns the given memories for any recall and records the query.</summary>
    private sealed class FakeMemory(params RecalledMemory[] toReturn) : IMemoryStore
    {
        public string? LastQuery { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task InsertSegmentAsync(string m, DateTime at, string s, string t, CancellationToken ct) => Task.CompletedTask;
        public Task StoreMemoryAsync(string m, MemoryKind kind, string t, string? person, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int topK, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<RecalledMemory>>(toReturn);
        }

        public Task<IReadOnlyList<TranscriptLine>> GetTranscriptAsync(string m, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranscriptLine>>([]);

        public Task<int> ClearMemoriesAsync(string? m, CancellationToken ct) => Task.FromResult(0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RelevantMemory_BelowThreshold_IsInjectedWithGuardFraming()
    {
        const string note = "We chose PostgreSQL with pgvector for storage.";
        var chat = new CannedChat("""{"advise": false, "kind": "tip", "advice": ""}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b", new FakeMemory(new RecalledMemory(MemoryKind.Decision, note, 0.20)));

        await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.Contains(note, chat.LastUser);
        Assert.Contains("only if directly relevant", chat.LastUser, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IrrelevantMemory_AboveThreshold_IsExcluded()
    {
        const string note = "Priya prefers async written updates.";
        var chat = new CannedChat("""{"advise": false, "kind": "tip", "advice": ""}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b", new FakeMemory(new RecalledMemory(MemoryKind.PersonFact, note, 0.55)));

        await advisor.ConsiderAsync(Context(), Context()[^1], CancellationToken.None);

        Assert.DoesNotContain(note, chat.LastUser);
    }

    [Fact]
    public async Task RecallQuery_IsTheLatestCaption()
    {
        // A focused, single-line query recalls a sharp question far better than a
        // diluted multi-line one (mixing in neighbours pushed the right memory out).
        var memory = new FakeMemory(); // returns nothing
        var chat = new CannedChat("""{"advise": false, "kind": "tip", "advice": ""}""");
        var advisor = new LlmAdvisor(chat, "qwen3:4b", memory);
        var context = MultiLineContext();

        await advisor.ConsiderAsync(context, context[^1], CancellationToken.None);

        Assert.Equal(context[^1].Caption, memory.LastQuery);
    }
}
