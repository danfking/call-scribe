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
    public static float[] RunningMean(ReadOnlySpan<float> centroid, int count, ReadOnlySpan<float> sample)
    {
        if (centroid.Length != sample.Length)
        {
            throw new ArgumentException($"Vector length mismatch: {centroid.Length} vs {sample.Length}.");
        }

        var result = new float[centroid.Length];
        for (var i = 0; i < centroid.Length; i++)
        {
            result[i] = (float)(((double)centroid[i] * count + sample[i]) / (count + 1));
        }
        return result;
    }
}
