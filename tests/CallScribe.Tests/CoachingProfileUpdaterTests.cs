using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Profiles;

namespace CallScribe.Tests;

public class CoachingProfileUpdaterTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 16, 0, 0);

    /// <summary>Returns canned markdown for each person, records every user prompt it saw, and can be
    /// told to throw for a particular person so per-person isolation is testable.</summary>
    private sealed class CannedChat(string reply, string? throwIfUserContains = null) : ICoachChat
    {
        public List<string> Users { get; } = [];

        public Task<string> CompleteAsync(string model, string system, string user, bool jsonMode, int maxTokens, CancellationToken ct)
        {
            Users.Add(user);
            if (throwIfUserContains != null && user.Contains(throwIfUserContains, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated model failure");
            return Task.FromResult(reply);
        }
    }

    /// <summary>Returns a fixed transcript for GetTranscriptAsync; the rest is unused here.</summary>
    private sealed class FakeTranscript(params TranscriptLine[] lines) : IMemoryStore
    {
        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task InsertSegmentAsync(string m, DateTime at, string s, string t, CancellationToken ct) => Task.CompletedTask;
        public Task StoreMemoryAsync(string m, MemoryKind kind, string t, string? person, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int topK, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecalledMemory>>([]);

        public Task<IReadOnlyList<TranscriptLine>> GetTranscriptAsync(string m, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranscriptLine>>(lines);

        public Task<int> ClearMemoriesAsync(string? m, CancellationToken ct) => Task.FromResult(0);
        public Task<int> RelabelAsync(string m, IReadOnlyDictionary<string, string> remap, CancellationToken ct) =>
            Task.FromResult(0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WritesProfilesOnlyForNamedNonSelfSpeakers()
    {
        var (store, dir) = NewStore();
        try
        {
            var memory = new FakeTranscript(
                new TranscriptLine(T0, "Me", "Hello."),
                new TranscriptLine(T0.AddSeconds(2), "Others", "Hi."),
                new TranscriptLine(T0.AddSeconds(4), "Speaker 1", "..."),
                new TranscriptLine(T0.AddSeconds(6), "Dan", "I'll lead."),       // self, excluded
                new TranscriptLine(T0.AddSeconds(8), "Gavin", "I have concerns."));
            var chat = new CannedChat("# Profile\nNotes.\n");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", memory, store, selfName: "Dan");

            var count = await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Equal(1, count);
            Assert.True(store.Exists("Gavin"));
            Assert.False(store.Exists("Me"));
            Assert.False(store.Exists("Others"));
            Assert.False(store.Exists("Speaker 1"));
            Assert.False(store.Exists("Dan"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PassesExistingProfileToTheModel()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Write("Gavin", "# Gavin\nExisting hand-written note.\n");
            var memory = new FakeTranscript(new TranscriptLine(T0, "Gavin", "Let's revisit the plan."));
            var chat = new CannedChat("# Gavin\nRefined.\n");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", memory, store, selfName: "Dan");

            await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Single(chat.Users);
            Assert.Contains("Existing hand-written note.", chat.Users[0]);
            Assert.Equal("# Gavin\nRefined.", store.Read("Gavin")!.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task OnePersonFailing_DoesNotStopTheRest()
    {
        var (store, dir) = NewStore();
        try
        {
            var memory = new FakeTranscript(
                new TranscriptLine(T0, "Boom", "I break the model."),
                new TranscriptLine(T0.AddSeconds(2), "Gavin", "I do not."));
            // The chat throws for the prompt that names "Boom", succeeds otherwise.
            var chat = new CannedChat("# Profile\nNotes.\n", throwIfUserContains: "Person to profile: Boom");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", memory, store, selfName: null);

            var count = await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Equal(1, count);
            Assert.True(store.Exists("Gavin"));
            Assert.False(store.Exists("Boom"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RefusalReply_DoesNotOverwriteExistingProfile()
    {
        var (store, dir) = NewStore();
        try
        {
            store.Write("Gavin", "# Gavin\nGood existing notes.\n");
            var memory = new FakeTranscript(new TranscriptLine(T0, "Gavin", "Hello."));
            // A reply that is not a profile (no leading heading) must not clobber the good file.
            var chat = new CannedChat("I'm sorry, I don't have enough information to build a profile.");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", memory, store, selfName: null);

            var count = await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Equal(0, count);
            Assert.Equal("# Gavin\nGood existing notes.", store.Read("Gavin")!.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task StripsWrappingCodeFence()
    {
        var (store, dir) = NewStore();
        try
        {
            var memory = new FakeTranscript(new TranscriptLine(T0, "Gavin", "Hello."));
            var chat = new CannedChat("```markdown\n# Gavin\nNotes.\n```");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", memory, store, selfName: null);

            await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Equal("# Gavin\nNotes.", store.Read("Gavin")!.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task EmptyTranscript_WritesNothing()
    {
        var (store, dir) = NewStore();
        try
        {
            var chat = new CannedChat("# Profile\nNotes.\n");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", new FakeTranscript(), store, selfName: null);

            var count = await updater.UpdateAsync("m1", CancellationToken.None);

            Assert.Equal(0, count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static (CoachingProfileStore Store, string Dir) NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "callscribe-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (new CoachingProfileStore(dir), dir);
    }
}
