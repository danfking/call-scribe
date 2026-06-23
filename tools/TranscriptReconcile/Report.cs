using System.Text;

namespace TranscriptReconcile;

/// <summary>Renders a reconciliation as a human-readable markdown report: a scorecard plus a
/// categorized, timestamped discrepancy list (the input the multi-agent analysis works from).</summary>
public static class Report
{
    private const double TimeErrorThresholdSec = 10;

    public static string Markdown(ReconResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {r.Label}").AppendLine();
        sb.AppendLine($"- **WER** {r.Wer:P1}  (S {r.Words.Substitutions} / D {r.Words.Deletions} / I {r.Words.Insertions} over {r.ReferenceWords} reference words; {r.HypothesisWords} hypothesis words)");
        sb.AppendLine($"- **CER** {r.Cer:P1}");
        sb.AppendLine($"- **Completeness** word recall {r.WordRecall:P1} (reference words captured), precision {r.WordPrecision:P1} (hypothesis words that belong)");
        sb.AppendLine($"- **Speakers** {r.Speakers.DistinctHypothesisLabels} labels → {r.Speakers.DistinctReferenceSpeakers} reference speakers; attribution {Pct(r.Speakers.CorrectlyAttributed, r.Speakers.CorrectlyAttributed + r.Speakers.Misattributed)} of words on the right speaker; {r.Speakers.FragmentedReferenceSpeakers} reference speaker(s) split across multiple labels");
        sb.AppendLine($"- **Timing** offset {r.Timing.OffsetSeconds:F1}s; abs error mean {r.Timing.MeanAbsErrorSec:F1}s / median {r.Timing.MedianAbsErrorSec:F1}s / p90 {r.Timing.P90AbsErrorSec:F1}s");
        sb.AppendLine();

        sb.AppendLine("Label → reference-name mapping (by overlap vote):");
        foreach (var (label, name) in r.Speakers.LabelToName.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  - `{label}` → {name}");
        }
        sb.AppendLine();

        var discrepancies = Discrepancies(r).ToList();
        sb.AppendLine($"### Line-level discrepancies ({discrepancies.Count})  _(boundary-sensitive; the scorecard above is word-level)_").AppendLine();
        foreach (var d in discrepancies) sb.AppendLine($"- {d}");
        return sb.ToString();
    }

    /// <summary>Each alignment step that isn't a clean match, tagged by type.</summary>
    public static IEnumerable<string> Discrepancies(ReconResult r)
    {
        foreach (var p in r.LineDiff)
        {
            if (p.Missing)
            {
                yield return $"`[missing]` {Fmt(p.Reference!)}";
            }
            else if (p.Spurious)
            {
                yield return $"`[spurious]` {Fmt(p.Hypothesis!)}";
            }
            else
            {
                var tags = new List<string>();
                if (WordError.Distance(WordError.Tokenize(p.Reference!.Text), WordError.Tokenize(p.Hypothesis!.Text)).Total > 0)
                {
                    tags.Add("mis-transcribed");
                }
                if (r.Speakers.LabelToName.TryGetValue(p.Hypothesis!.Speaker, out var to) && to != p.Reference!.Speaker)
                {
                    tags.Add("mis-attributed");
                }
                if (Math.Abs((p.Hypothesis!.StartSec - r.Timing.OffsetSeconds) - p.Reference!.StartSec) > TimeErrorThresholdSec)
                {
                    tags.Add("mis-timed");
                }
                if (tags.Count > 0)
                {
                    yield return $"`[{string.Join("+", tags)}]` ref {Fmt(p.Reference!)}  ‖  hyp {Fmt(p.Hypothesis!)}";
                }
            }
        }
    }

    private static string Fmt(Utterance u) =>
        $"{TimeSpan.FromSeconds(Math.Max(0, u.StartSec)):mm\\:ss} **{u.Speaker}**: {Clip(u.Text)}";

    private static string Clip(string t) => t.Length <= 90 ? t : t[..87] + "…";

    private static string Pct(int num, int den) => den == 0 ? "n/a" : $"{(double)num / den:P0}";
}
