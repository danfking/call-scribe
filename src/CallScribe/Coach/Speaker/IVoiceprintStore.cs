namespace CallScribe.Coach.Speaker;

/// <summary>A nearest match from the enrolled voiceprints, with its cosine distance to the
/// query embedding (smaller = more confident it is that person).</summary>
public readonly record struct VoiceprintMatch(string PersonName, double Distance);

/// <summary>Durable store of enrolled voiceprints — one averaged embedding per named
/// person — so that once a voice is named it is recognised automatically in future
/// meetings. Backed by pgvector in the same Postgres instance as the memory store.</summary>
public interface IVoiceprintStore : IAsyncDisposable
{
    /// <summary>Create the voiceprints table and its vector index if absent.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Nearest enrolled person to <paramref name="embedding"/>, or null if none are
    /// enrolled. The caller decides whether the distance is close enough to trust.</summary>
    Task<VoiceprintMatch?> IdentifyAsync(IReadOnlyList<float> embedding, CancellationToken ct);

    /// <summary>Cosine distance from <paramref name="embedding"/> to one specific person's
    /// voiceprint, or null if that person isn't enrolled. Used to verify "is this me?" rather
    /// than "who is the nearest of everyone?".</summary>
    Task<double?> DistanceToAsync(string personName, IReadOnlyList<float> embedding, CancellationToken ct);

    /// <summary>Enroll a sample for a person, folding it into their averaged voiceprint
    /// (creating the person on first enrollment).</summary>
    Task EnrollAsync(string personName, IReadOnlyList<float> embedding, CancellationToken ct);

    /// <summary>Rename an enrolled person's voiceprint (e.g. a live /rename), replacing any
    /// existing print under the new name. Returns false if no print existed under the old name.</summary>
    Task<bool> RenameAsync(string oldName, string newName, CancellationToken ct);

    /// <summary>Names of everyone with an enrolled voiceprint, alphabetical.</summary>
    Task<IReadOnlyList<string>> ListPeopleAsync(CancellationToken ct);

    /// <summary>Delete voiceprints — all of them, or just one person's. Returns the count deleted.</summary>
    Task<int> ForgetAsync(string? personName, CancellationToken ct);
}
