using CallScribe.Coach.Llm;
using CallScribe.Coach.Profiles;

namespace CallScribe.Tests;

public class CoachingProfileUpdaterTests
{
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

    [Fact]
    public async Task WritesProfilesOnlyForNamedNonSelfSpeakers()
    {
        var (store, dir) = NewStore();
        try
        {
            var lines = new List<(string, string)>
            {
                ("Me", "Hello."),
                ("Others", "Hi."),
                ("Speaker 1", "..."),
                ("Dan", "I'll lead."),     // self, excluded
                ("Gavin", "I have concerns."),
            };
            var chat = new CannedChat("# Profile\nNotes.\n");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: "Dan");

            var count = await updater.UpdateAsync(lines, CancellationToken.None);

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
            var chat = new CannedChat("# Gavin\nRefined.\n");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: "Dan");

            await updater.UpdateAsync([("Gavin", "Let's revisit the plan.")], CancellationToken.None);

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
            var lines = new List<(string, string)> { ("Boom", "I break the model."), ("Gavin", "I do not.") };
            // The chat throws for the prompt that names "Boom", succeeds otherwise.
            var chat = new CannedChat("# Profile\nNotes.\n", throwIfUserContains: "Person to profile: Boom");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: null);

            var count = await updater.UpdateAsync(lines, CancellationToken.None);

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
            // A reply that is not a profile (no leading heading) must not clobber the good file.
            var chat = new CannedChat("I'm sorry, I don't have enough information to build a profile.");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: null);

            var count = await updater.UpdateAsync([("Gavin", "Hello.")], CancellationToken.None);

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
            var chat = new CannedChat("```markdown\n# Gavin\nNotes.\n```");
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: null);

            await updater.UpdateAsync([("Gavin", "Hello.")], CancellationToken.None);

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
            var updater = new CoachingProfileUpdater(chat, "llama3.1:8b", store, selfName: null);

            var count = await updater.UpdateAsync([], CancellationToken.None);

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
