using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace CallScribe.Coach.Memory;

/// <summary>Postgres-backed memory store: TimescaleDB for the realtime transcript
/// hypertable and pgvector for semantic recall, in one database. Schema is created in
/// code (EnsureSchemaAsync) so the container only needs the image.</summary>
public sealed class MemoryStore : IMemoryStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbedder _embedder;

    public MemoryStore(string connectionString, IEmbedder embedder)
    {
        _connectionString = connectionString;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _embedder = embedder;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var sql = $"""
            CREATE EXTENSION IF NOT EXISTS timescaledb;
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS transcript_segments (
              meeting_id text NOT NULL,
              at         timestamptz NOT NULL,
              speaker    text NOT NULL,
              text       text NOT NULL
            );
            SELECT create_hypertable('transcript_segments', 'at', if_not_exists => TRUE, migrate_data => TRUE);

            CREATE TABLE IF NOT EXISTS memories (
              id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
              meeting_id text NOT NULL,
              kind       text NOT NULL,
              text       text NOT NULL,
              person     text,
              embedding  vector({_embedder.Dimensions}) NOT NULL,
              created_at timestamptz NOT NULL DEFAULT now()
            );
            -- Backfill for databases created before the person column existed.
            ALTER TABLE memories ADD COLUMN IF NOT EXISTS person text;
            CREATE INDEX IF NOT EXISTS memories_embedding_idx
              ON memories USING hnsw (embedding vector_cosine_ops);
            """;

        // Run DDL on a plain connection so the 'vector' extension exists before the
        // typed data source first connects — otherwise Npgsql caches a type catalog
        // without 'vector' and every Vector parameter then fails to bind.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Force the typed data source to (re)load its catalog now that 'vector' exists.
        await using var reload = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await reload.ReloadTypesAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertSegmentAsync(string meetingId, DateTime at, string speaker, string text, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO transcript_segments (meeting_id, at, speaker, text) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = meetingId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = speaker });
        cmd.Parameters.Add(new NpgsqlParameter { Value = text });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task StoreMemoryAsync(string meetingId, MemoryKind kind, string text, string? person, CancellationToken ct)
    {
        var embedding = await _embedder.EmbedAsync(text, EmbedPurpose.Document, ct).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO memories (meeting_id, kind, text, person, embedding) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = meetingId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = kind.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)person ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(embedding) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int topK, CancellationToken ct)
    {
        var embedding = await _embedder.EmbedAsync(query, EmbedPurpose.Query, ct).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand(
            "SELECT kind, text, person, embedding <=> $1 AS distance FROM memories ORDER BY embedding <=> $1 LIMIT $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(embedding) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = topK });

        var results = new List<RecalledMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = Enum.TryParse<MemoryKind>(reader.GetString(0), out var k) ? k : MemoryKind.Insight;
            var person = reader.IsDBNull(2) ? null : reader.GetString(2);
            results.Add(new RecalledMemory(kind, reader.GetString(1), reader.GetDouble(3), person));
        }
        return results;
    }

    public async Task<IReadOnlyList<TranscriptLine>> GetTranscriptAsync(string meetingId, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT at, speaker, text FROM transcript_segments WHERE meeting_id = $1 ORDER BY at");
        cmd.Parameters.Add(new NpgsqlParameter { Value = meetingId });

        var segments = new List<TranscriptLine>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            segments.Add(new TranscriptLine(
                reader.GetDateTime(0), reader.GetString(1), reader.GetString(2)));
        }
        return segments;
    }

    public async Task<int> ClearMemoriesAsync(string? meetingId, CancellationToken ct)
    {
        var sql = meetingId is null
            ? "DELETE FROM memories"
            : "DELETE FROM memories WHERE meeting_id = $1";
        await using var cmd = _dataSource.CreateCommand(sql);
        if (meetingId is not null) cmd.Parameters.Add(new NpgsqlParameter { Value = meetingId });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync().ConfigureAwait(false);
}
