namespace TranscriptReconcile;

/// <summary>One step of an alignment: a matched pair, a reference line with no match (Missing),
/// or a hypothesis line with no match (Spurious).</summary>
public sealed record AlignedPair(Utterance? Reference, Utterance? Hypothesis)
{
    public bool Matched => Reference is not null && Hypothesis is not null;
    public bool Missing => Reference is not null && Hypothesis is null;   // in reference, dropped by hypothesis
    public bool Spurious => Reference is null && Hypothesis is not null;  // hypothesis invented it
}

public static class Aligner
{
    /// <summary>Estimate the time offset (hypothesis − reference, seconds) that lines the two
    /// streams up, from confident text matches. Robust median; 0 when there are no clear anchors.</summary>
    public static double EstimateOffsetSeconds(IReadOnlyList<Utterance> reference, IReadOnlyList<Utterance> hypothesis)
    {
        var refTok = reference.Select(u => WordError.Tokenize(u.Text)).ToArray();
        var hypTok = hypothesis.Select(u => WordError.Tokenize(u.Text)).ToArray();
        var deltas = new List<double>();
        for (var i = 0; i < reference.Count; i++)
        {
            if (refTok[i].Length < 3) continue; // anchor only on substantial lines
            var bestSim = 0.0;
            var bestJ = -1;
            for (var j = 0; j < hypothesis.Count; j++)
            {
                var sim = WordError.Similarity(refTok[i], hypTok[j]);
                if (sim > bestSim) { bestSim = sim; bestJ = j; }
            }
            if (bestJ >= 0 && bestSim >= 0.7) deltas.Add(hypothesis[bestJ].StartSec - reference[i].StartSec);
        }
        if (deltas.Count == 0) return 0.0;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    /// <summary>Monotonic Needleman–Wunsch alignment. A match scores text similarity minus a mild
    /// penalty for time distance (after applying <paramref name="offsetSeconds"/> to the hypothesis);
    /// unmatched lines become Missing/Spurious. <paramref name="gapScore"/> is the value of leaving a
    /// line unmatched, so a pair is matched only when its score beats gapping both, roughly when
    /// similarity exceeds 2·gapScore (≈0.5 at the default).</summary>
    public static IReadOnlyList<AlignedPair> Align(
        IReadOnlyList<Utterance> reference, IReadOnlyList<Utterance> hypothesis,
        double offsetSeconds = 0, double gapScore = 0.25, double timeWeight = 0.25, double timeToleranceSec = 60)
    {
        int n = reference.Count, m = hypothesis.Count;
        var refTok = reference.Select(u => WordError.Tokenize(u.Text)).ToArray();
        var hypTok = hypothesis.Select(u => WordError.Tokenize(u.Text)).ToArray();

        double MatchScore(int i, int j)
        {
            var sim = WordError.Similarity(refTok[i], hypTok[j]);
            var dt = Math.Abs((hypothesis[j].StartSec - offsetSeconds) - reference[i].StartSec);
            return sim - timeWeight * Math.Min(1.0, dt / timeToleranceSec);
        }

        var dp = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++) dp[i, 0] = dp[i - 1, 0] + gapScore;
        for (var j = 1; j <= m; j++) dp[0, j] = dp[0, j - 1] + gapScore;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                dp[i, j] = Math.Max(Math.Max(
                    dp[i - 1, j - 1] + MatchScore(i - 1, j - 1),
                    dp[i - 1, j] + gapScore),
                    dp[i, j - 1] + gapScore);
            }
        }

        var pairs = new List<AlignedPair>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && dp[i, j] == dp[i - 1, j - 1] + MatchScore(i - 1, j - 1))
            {
                pairs.Add(new AlignedPair(reference[i - 1], hypothesis[j - 1])); i--; j--;
            }
            else if (i > 0 && dp[i, j] == dp[i - 1, j] + gapScore)
            {
                pairs.Add(new AlignedPair(reference[i - 1], null)); i--;
            }
            else
            {
                pairs.Add(new AlignedPair(null, hypothesis[j - 1])); j--;
            }
        }
        pairs.Reverse();
        return pairs;
    }
}
