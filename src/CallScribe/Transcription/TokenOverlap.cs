namespace CallScribe.Transcription;

/// <summary>Word-set tokenisation and overlap scoring shared by the cross-track echo filter
/// (speaker bleed) and the advice filter (repeat advice). Both reduce text to a lowercase set of
/// word tokens and score similarity by overlap coefficient; only the minimum-token gate differs.</summary>
internal static class TokenOverlap
{
    private static readonly char[] Delimiters =
        [' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')', '[', ']', '-'];

    /// <summary>Lowercase the text and split it into a set of distinct word tokens.</summary>
    public static HashSet<string> Tokenize(string text) =>
        text.ToLowerInvariant().Split(Delimiters, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>Overlap coefficient: shared words divided by the smaller set's size (1.0 when one set
    /// contains the other, 0 when disjoint). Returns 0 when the smaller set has fewer than
    /// <paramref name="minTokens"/> words, which also guards the empty-set divide; raise it so
    /// trivially short matches ("yeah" vs "yeah") are not treated as overlap.</summary>
    public static double OverlapCoefficient(HashSet<string> a, HashSet<string> b, int minTokens = 1)
    {
        var smaller = Math.Min(a.Count, b.Count);
        if (smaller < minTokens) return 0;
        var intersection = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)intersection / smaller;
    }
}
