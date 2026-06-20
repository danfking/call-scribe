namespace CallScribe.Coach.Memory;

/// <summary>Realtime + semantic memory. Transcript segments are time-series rows written
/// live during a meeting; memories are durable, embedded facts written at consolidation
/// and recalled semantically in future meetings.</summary>
public interface IMemoryStore : IAsyncDisposable
{
    /// <summary>Create extensions, tables, the hypertable, and the vector index if absent.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Persist one live caption to the realtime time-series table.</summary>
    Task InsertSegmentAsync(string meetingId, DateTime at, string speaker, string text, CancellationToken ct);

    /// <summary>Embed and store a durable memory for future semantic recall.</summary>
    Task StoreMemoryAsync(string meetingId, MemoryKind kind, string text, CancellationToken ct);

    /// <summary>Return the <paramref name="topK"/> memories closest to <paramref name="query"/>.</summary>
    Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int topK, CancellationToken ct);

    /// <summary>Read back a meeting's transcript in time order (for consolidation).</summary>
    Task<IReadOnlyList<TranscriptLine>> GetTranscriptAsync(string meetingId, CancellationToken ct);

    /// <summary>Delete stored memories — all of them, or just one meeting's when
    /// <paramref name="meetingId"/> is given. Returns the number deleted.</summary>
    Task<int> ClearMemoriesAsync(string? meetingId, CancellationToken ct);
}
