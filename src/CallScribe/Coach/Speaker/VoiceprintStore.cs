using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace CallScribe.Coach.Speaker;

/// <summary>pgvector-backed store of enrolled voiceprints: one averaged embedding per
/// named person. Lives in the same Postgres instance as the memory store. Enrollment
/// folds each new sample into the person's running mean (computed in C# rather than in SQL
/// so it does not depend on pgvector's arithmetic operators being present).</summary>
public sealed class VoiceprintStore : IVoiceprintStore
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _dimensions;

    public VoiceprintStore(string connectionString, int dimensions)
    {
        _connectionString = connectionString;
        _dimensions = dimensions;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var sql = $"""
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS voiceprints (
              person_name  text PRIMARY KEY,
              embedding    vector({_dimensions}) NOT NULL,
              sample_count int NOT NULL DEFAULT 1,
              created_at   timestamptz NOT NULL DEFAULT now(),
              updated_at   timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS voiceprints_embedding_idx
              ON voiceprints USING hnsw (embedding vector_cosine_ops);
            """;

        // DDL on a plain connection so the 'vector' extension exists before the typed data
        // source first connects (mirrors MemoryStore — otherwise Npgsql caches a catalog
        // without 'vector' and Vector parameters fail to bind).
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var reload = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await reload.ReloadTypesAsync(ct).ConfigureAwait(false);
    }

    public async Task<VoiceprintMatch?> IdentifyAsync(IReadOnlyList<float> embedding, CancellationToken ct)
    {
        var query = ToArray(embedding);
        // A dimension mismatch (e.g. the embed model was swapped after enrollment) would make
        // the pgvector <=> operator throw; degrade to "no match" so the caller falls back to
        // session clustering rather than failing the whole pass.
        if (query.Length != _dimensions) return null;

        await using var cmd = _dataSource.CreateCommand(
            "SELECT person_name, embedding <=> $1 AS distance FROM voiceprints ORDER BY embedding <=> $1 LIMIT 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(query) });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new VoiceprintMatch(reader.GetString(0), reader.GetDouble(1));
    }

    public async Task EnrollAsync(string personName, IReadOnlyList<float> embedding, CancellationToken ct)
    {
        var sample = ToArray(embedding);
        if (sample.Length != _dimensions)
        {
            throw new ArgumentException($"Voiceprint has {sample.Length} dims, expected {_dimensions}.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        float[] merged;
        int count;
        await using (var read = new NpgsqlCommand(
            "SELECT embedding, sample_count FROM voiceprints WHERE person_name = $1 FOR UPDATE", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { Value = personName });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var existing = reader.GetFieldValue<Vector>(0).ToArray();
                count = reader.GetInt32(1);
                merged = VectorMath.RunningMean(existing, count, sample);
                count += 1;
            }
            else
            {
                merged = sample;
                count = 1;
            }
        }

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO voiceprints (person_name, embedding, sample_count)
            VALUES ($1, $2, $3)
            ON CONFLICT (person_name) DO UPDATE
              SET embedding = EXCLUDED.embedding,
                  sample_count = EXCLUDED.sample_count,
                  updated_at = now()
            """, conn, tx))
        {
            upsert.Parameters.Add(new NpgsqlParameter { Value = personName });
            upsert.Parameters.Add(new NpgsqlParameter { Value = new Vector(merged) });
            upsert.Parameters.Add(new NpgsqlParameter { Value = count });
            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListPeopleAsync(CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT person_name FROM voiceprints ORDER BY person_name");
        var people = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            people.Add(reader.GetString(0));
        }
        return people;
    }

    public async Task<int> ForgetAsync(string? personName, CancellationToken ct)
    {
        var sql = personName is null
            ? "DELETE FROM voiceprints"
            : "DELETE FROM voiceprints WHERE person_name = $1";
        await using var cmd = _dataSource.CreateCommand(sql);
        if (personName is not null) cmd.Parameters.Add(new NpgsqlParameter { Value = personName });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static float[] ToArray(IReadOnlyList<float> v) => v as float[] ?? [.. v];

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync().ConfigureAwait(false);
}
