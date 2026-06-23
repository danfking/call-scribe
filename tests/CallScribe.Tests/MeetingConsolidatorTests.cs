using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;

namespace CallScribe.Tests;

public class MeetingConsolidatorTests
{
    private sealed class CannedChat(string reply) : ICoachChat
    {
        public Task<string> CompleteAsync(string model, string system, string user, bool jsonMode, int maxTokens, CancellationToken ct)
            => Task.FromResult(reply);
    }

    /// <summary>Returns a fixed transcript and captures stored memories.</summary>
    private sealed class CapturingStore(IReadOnlyList<TranscriptLine> transcript) : IMemoryStore
    {
        public readonly List<(MemoryKind Kind, string Text, string? Person)> Stored = [];

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task InsertSegmentAsync(string m, DateTime at, string s, string t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string q, int k, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecalledMemory>>([]);
        public Task<IReadOnlyList<TranscriptLine>> GetTranscriptAsync(string m, CancellationToken ct) =>
            Task.FromResult(transcript);
        public Task<int> ClearMemoriesAsync(string? m, CancellationToken ct) => Task.FromResult(0);
        public Task<int> RelabelAsync(string m, IReadOnlyDictionary<string, string> remap, CancellationToken ct) =>
            Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task StoreMemoryAsync(string m, MemoryKind kind, string text, string? person, CancellationToken ct)
        {
            Stored.Add((kind, text, person));
            return Task.CompletedTask;
        }
    }

    private static IReadOnlyList<TranscriptLine> SampleTranscript() =>
        [new TranscriptLine(new DateTime(2026, 6, 11, 16, 0, 0), "Others", "Let's ship dual-track first.")];

    [Fact]
    public async Task ExtractedItems_AreStoredWithMappedKinds()
    {
        var reply = """
            {"items": [
              {"kind": "decision", "text": "Ship dual-track capture before live captions."},
              {"kind": "action_item", "text": "Send latency benchmarks to the platform team."},
              {"kind": "bogus", "text": "Unknown kind falls back to insight."}
            ]}
            """;
        var store = new CapturingStore(SampleTranscript());
        var consolidator = new MeetingConsolidator(new CannedChat(reply), "llama3.1:8b", store);

        var count = await consolidator.ConsolidateAsync("m1", CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(MemoryKind.Decision, store.Stored[0].Kind);
        Assert.Equal(MemoryKind.ActionItem, store.Stored[1].Kind);
        Assert.Equal(MemoryKind.Insight, store.Stored[2].Kind); // unknown -> insight
    }

    [Fact]
    public async Task EmptyTranscript_StoresNothing_AndSkipsTheModel()
    {
        var store = new CapturingStore([]);
        var consolidator = new MeetingConsolidator(new CannedChat("should not be used"), "llama3.1:8b", store);

        var count = await consolidator.ConsolidateAsync("m1", CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task PersonAttribution_FlowsThrough_AndIsNullWhenOmitted()
    {
        var reply = """
            {"items": [
              {"kind": "person_fact", "text": "Allergic to peanuts.", "person": "Priya"},
              {"kind": "decision", "text": "Ship dual-track first."}
            ]}
            """;
        var store = new CapturingStore(SampleTranscript());
        var consolidator = new MeetingConsolidator(new CannedChat(reply), "llama3.1:8b", store);

        await consolidator.ConsolidateAsync("m1", CancellationToken.None);

        Assert.Equal("Priya", store.Stored[0].Person);
        Assert.Null(store.Stored[1].Person);
    }

    [Fact]
    public async Task BlankItemText_IsSkipped()
    {
        var reply = """{"items": [{"kind": "insight", "text": "  "}, {"kind": "decision", "text": "Real one."}]}""";
        var store = new CapturingStore(SampleTranscript());
        var consolidator = new MeetingConsolidator(new CannedChat(reply), "llama3.1:8b", store);

        var count = await consolidator.ConsolidateAsync("m1", CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal("Real one.", store.Stored[0].Text);
    }
}
