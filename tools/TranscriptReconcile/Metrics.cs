namespace TranscriptReconcile;

public sealed record SpeakerStats(
    int DistinctReferenceSpeakers,
    int DistinctHypothesisLabels,
    int CorrectlyAttributed,
    int Misattributed,
    int FragmentedReferenceSpeakers,
    IReadOnlyDictionary<string, string> LabelToName);

public sealed record TimingStats(double OffsetSeconds, double MeanAbsErrorSec, double MedianAbsErrorSec, double P90AbsErrorSec);

/// <summary>The full reconciliation of one hypothesis transcript against a reference. Transcription
/// (WER/CER), completeness (word recall/precision), speakers, and timing are all derived from a
/// single word-level alignment, so they don't depend on how each transcript chose line boundaries.
/// <see cref="LineDiff"/> is a separate line-level alignment kept only for the human discrepancy list.</summary>
public sealed record ReconResult(
    string Label,
    WordError.EditCounts Words,
    WordError.EditCounts Chars,
    int ReferenceWords,
    int HypothesisWords,
    SpeakerStats Speakers,
    TimingStats Timing,
    IReadOnlyList<AlignedPair> LineDiff)
{
    public double Wer => Words.Rate;
    public double Cer => Chars.Rate;
    public double WordRecall => ReferenceWords == 0 ? 0 : 1.0 - (double)Words.Deletions / ReferenceWords;
    public double WordPrecision => HypothesisWords == 0 ? 0 : 1.0 - (double)Words.Insertions / HypothesisWords;
}

public static class Metrics
{
    public static ReconResult Compute(string label, IReadOnlyList<Utterance> reference, IReadOnlyList<Utterance> hypothesis)
    {
        var offset = Aligner.EstimateOffsetSeconds(reference, hypothesis);

        var refWords = WordStream.Flatten(reference);
        var hypWords = WordStream.Flatten(hypothesis);
        var pairs = WordStream.Align(refWords, hypWords);

        int sub = 0, del = 0, ins = 0;
        var timeErrors = new List<double>();
        var votes = new Dictionary<string, Dictionary<string, int>>(); // hyp label -> (ref name -> count)

        foreach (var p in pairs)
        {
            if (p.Reference is { } r && p.Hypothesis is { } h)
            {
                if (r.Token != h.Token) sub++;
                if (!votes.TryGetValue(h.Speaker, out var d)) votes[h.Speaker] = d = [];
                d[r.Speaker] = d.GetValueOrDefault(r.Speaker) + 1;
                timeErrors.Add(Math.Abs((h.TimeSec - offset) - r.TimeSec));
            }
            else if (p.Reference is not null) del++;
            else ins++;
        }

        var words = new WordError.EditCounts(sub, del, ins, refWords.Count);
        var chars = WordError.CharDistance(
            string.Join(' ', reference.Select(u => u.Text)),
            string.Join(' ', hypothesis.Select(u => u.Text)));

        // Map each hypothesis speaker label to the reference name it most often co-occurs with.
        var map = votes.ToDictionary(kv => kv.Key, kv => kv.Value.MaxBy(x => x.Value).Key);
        int correct = 0, mis = 0;
        foreach (var p in pairs)
        {
            if (p.Reference is not { } r || p.Hypothesis is not { } h) continue;
            if (map.TryGetValue(h.Speaker, out var to) && to == r.Speaker) correct++;
            else mis++;
        }

        var refToLabels = new Dictionary<string, HashSet<string>>();
        foreach (var (hl, rn) in map)
        {
            if (!refToLabels.TryGetValue(rn, out var s)) refToLabels[rn] = s = [];
            s.Add(hl);
        }
        var fragmented = refToLabels.Count(kv => kv.Value.Count > 1);

        var speakers = new SpeakerStats(
            reference.Select(u => u.Speaker).Distinct().Count(),
            hypothesis.Select(u => u.Speaker).Distinct().Count(),
            correct, mis, fragmented, map);

        timeErrors.Sort();
        var timing = new TimingStats(
            offset,
            timeErrors.Count > 0 ? timeErrors.Average() : 0,
            timeErrors.Count > 0 ? timeErrors[timeErrors.Count / 2] : 0,
            timeErrors.Count > 0 ? timeErrors[Math.Min(timeErrors.Count - 1, (int)(timeErrors.Count * 0.9))] : 0);

        var lineDiff = Aligner.Align(reference, hypothesis, offset);
        return new ReconResult(label, words, chars, refWords.Count, hypWords.Count, speakers, timing, lineDiff);
    }
}
