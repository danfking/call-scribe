using CallScribe.Coach.Speaker;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class SpeakerResolverTests
{
    /// <summary>Voiceprint store stub: returns a fixed nearest match (or none) and records
    /// enrollments, so resolver logic is tested without Postgres.</summary>
    private sealed class FakeVoiceprints(VoiceprintMatch? match) : IVoiceprintStore
    {
        public readonly List<(string Person, float[] Embedding)> Enrolled = [];

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<VoiceprintMatch?> IdentifyAsync(IReadOnlyList<float> e, CancellationToken ct) =>
            Task.FromResult(match);
        public Task<double?> DistanceToAsync(string person, IReadOnlyList<float> e, CancellationToken ct) =>
            Task.FromResult<double?>(match is { } m && m.PersonName == person ? m.Distance : null);
        public Task EnrollAsync(string person, IReadOnlyList<float> e, CancellationToken ct)
        {
            Enrolled.Add((person, e as float[] ?? [.. e]));
            return Task.CompletedTask;
        }
        public Task<bool> RenameAsync(string oldName, string newName, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListPeopleAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<int> ForgetAsync(string? person, CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void AssignSession_GroupsCloseVoices_AndSplitsDistantOnes()
    {
        var resolver = new SpeakerResolver(store: null);

        var a1 = resolver.AssignSession([1f, 0f, 0f, 0f]);
        var a2 = resolver.AssignSession([0.9f, 0.1f, 0f, 0f]); // ~same direction as a1
        var b1 = resolver.AssignSession([0f, 1f, 0f, 0f]);     // orthogonal -> new
        var b2 = resolver.AssignSession([0.05f, 0.95f, 0f, 0f]); // ~same as b1

        Assert.Equal(a1, a2);
        Assert.Equal(b1, b2);
        Assert.NotEqual(a1, b1);
        Assert.Equal("Speaker 1", a1);
        Assert.Equal("Speaker 2", b1);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsEnrolledName_WhenWithinThreshold()
    {
        var store = new FakeVoiceprints(new VoiceprintMatch("Gavin", 0.12));
        var resolver = new SpeakerResolver(store, enrolledMaxDistance: 0.30);

        var name = await resolver.ResolveAsync([1f, 0f, 0f], CancellationToken.None);

        Assert.Equal("Gavin", name);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToSessionSpeaker_WhenMatchTooFar()
    {
        var store = new FakeVoiceprints(new VoiceprintMatch("Gavin", 0.80)); // beyond threshold
        var resolver = new SpeakerResolver(store, enrolledMaxDistance: 0.30);

        var name = await resolver.ResolveAsync([1f, 0f, 0f], CancellationToken.None);

        Assert.Equal("Speaker 1", name);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToSessionSpeaker_WhenNoneEnrolled()
    {
        var store = new FakeVoiceprints(match: null);
        var resolver = new SpeakerResolver(store);

        var name = await resolver.ResolveAsync([1f, 0f, 0f], CancellationToken.None);

        Assert.Equal("Speaker 1", name);
    }

    [Fact]
    public void EmptyEmbedding_StaysGenericFarSideLabel()
    {
        var resolver = new SpeakerResolver(store: null);

        Assert.Equal(LiveCaptionEngine.OthersLabel, resolver.AssignSession([]));
    }

    [Fact]
    public void Rename_RelabelsSessionSpeaker_SoFutureCaptionsUseTheNewName()
    {
        var resolver = new SpeakerResolver(store: null);
        var label = resolver.AssignSession([1f, 0f, 0f]); // "Speaker 1"

        var centroid = resolver.Rename(label, "Sammy");

        Assert.NotNull(centroid);
        Assert.Equal("Sammy", resolver.AssignSession([0.95f, 0.05f, 0f])); // same voice -> new name
    }

    [Fact]
    public void Rename_ReturnsNull_ForUnknownLabel()
    {
        var resolver = new SpeakerResolver(store: null);
        Assert.Null(resolver.Rename("Speaker 9", "Nobody"));
    }

    [Fact]
    public async Task EnrolledMatch_IsRegisteredInSession_SoItCanBeRenamed()
    {
        // Regression: a speaker recognised from the voiceprint store ("Joe") must still be
        // renameable live (/rename "Joe" "Bob"), which needs a session entry to exist.
        var store = new FakeVoiceprints(new VoiceprintMatch("Joe", 0.10));
        var resolver = new SpeakerResolver(store, enrolledMaxDistance: 0.30);

        var name = await resolver.ResolveAsync([1f, 0f, 0f], CancellationToken.None);
        Assert.Equal("Joe", name);

        Assert.NotNull(resolver.Rename("Joe", "Bob"));
    }
}

public class SelfVerificationTests
{
    [Fact]
    public void NullDistance_KeepsAsMe_NoOpinion()
    {
        // Self not enrolled, or clip too short to embed: never drop the user's speech.
        var result = SpeakerIdentity.DecideMe(null, 0.45, "Dan");
        Assert.False(result.IsBleed);
        Assert.Null(result.Name);
    }

    [Fact]
    public void CloseDistance_KeepsAndLabelsWithName()
    {
        var result = SpeakerIdentity.DecideMe(0.20, 0.45, "Dan");
        Assert.False(result.IsBleed);
        Assert.Equal("Dan", result.Name);
    }

    [Fact]
    public void FarDistance_FlaggedAsBleed()
    {
        // Voice clearly isn't the user (far-side bleed on the mic) -> suppress.
        var result = SpeakerIdentity.DecideMe(0.70, 0.45, "Dan");
        Assert.True(result.IsBleed);
        Assert.Null(result.Name);
    }

    [Fact]
    public void AtThreshold_CountsAsMe()
    {
        var result = SpeakerIdentity.DecideMe(0.45, 0.45, "Dan");
        Assert.False(result.IsBleed);
        Assert.Equal("Dan", result.Name);
    }
}

public class VectorMathTests
{
    [Fact]
    public void CosineDistance_IsZeroForSameDirection_OneForOrthogonal()
    {
        Assert.Equal(0.0, VectorMath.CosineDistance([1f, 0f], [2f, 0f]), 6); // same direction, any scale
        Assert.Equal(1.0, VectorMath.CosineDistance([1f, 0f], [0f, 1f]), 6); // orthogonal
        Assert.Equal(2.0, VectorMath.CosineDistance([1f, 0f], [-1f, 0f]), 6); // opposite
    }

    [Fact]
    public void CosineDistance_TreatsZeroVectorAsMaximallyDistant()
    {
        Assert.Equal(2.0, VectorMath.CosineDistance([0f, 0f], [1f, 0f]), 6);
    }

    [Fact]
    public void RunningMean_AveragesFoldedSamples()
    {
        // mean of [0,0] (count 1) folded with [2,4] -> [1,2]
        var mean = VectorMath.RunningMean([0f, 0f], 1, [2f, 4f]);
        Assert.Equal([1f, 2f], mean);

        // fold a third sample [4,4] into centroid [1,2] built from 2 samples -> [2, 8/3]
        var mean2 = VectorMath.RunningMean(mean, 2, [4f, 4f]);
        Assert.Equal(2f, mean2[0], 5);
        Assert.Equal(8f / 3f, mean2[1], 5);
    }
}
