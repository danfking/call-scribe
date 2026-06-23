namespace CallScribe.Coach.Speaker;

/// <summary>Small vector helpers for comparing and averaging speaker embeddings.
/// Distances are cosine distance (1 - cosine similarity): 0 = same direction,
/// 1 = orthogonal, 2 = opposite. Speaker embeddings are compared by direction, so
/// cosine is the right metric (and matches pgvector's vector_cosine_ops index).</summary>
public static class VectorMath
{
    /// <summary>Cosine distance between two equal-length vectors. A zero-magnitude vector
    /// has no direction, so it is treated as maximally distant (2).</summary>
    public static double CosineDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector length mismatch: {a.Length} vs {b.Length}.");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 2.0;
        var similarity = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return 1.0 - similarity;
    }

    /// <summary>Running mean: fold one new sample into an averaged centroid built from
    /// <paramref name="count"/> prior samples. Returns a new array.</summary>
    public static float[] RunningMean(ReadOnlySpan<float> centroid, int count, ReadOnlySpan<float> sample) =>
        WeightedMean(centroid, count, sample, 1);

    /// <summary>Weighted mean of two centroids, each standing for <paramref name="countA"/> /
    /// <paramref name="countB"/> averaged samples. Used to fold one speaker cluster into another.
    /// Accumulates in double so a long-running centroid does not lose precision. Returns a new array.</summary>
    public static float[] WeightedMean(ReadOnlySpan<float> a, int countA, ReadOnlySpan<float> b, int countB)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector length mismatch: {a.Length} vs {b.Length}.");
        }

        var total = countA + countB;
        var result = new float[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            result[i] = (float)(((double)a[i] * countA + (double)b[i] * countB) / total);
        }
        return result;
    }
}
