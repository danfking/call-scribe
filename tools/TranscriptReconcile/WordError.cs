namespace TranscriptReconcile;

/// <summary>Word/character error rate via token-level edit distance, plus a normalized
/// similarity used to score alignment. No such helper exists in the app; this is the core.</summary>
public static class WordError
{
    /// <summary>Lowercase, strip punctuation, split on whitespace into comparable word tokens.</summary>
    public static string[] Tokenize(string text)
    {
        var raw = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>(raw.Length);
        foreach (var word in raw)
        {
            var stripped = new string([.. word.Where(char.IsLetterOrDigit)]);
            if (stripped.Length > 0) tokens.Add(stripped);
        }
        return [.. tokens];
    }

    public readonly record struct EditCounts(int Substitutions, int Deletions, int Insertions, int ReferenceLength)
    {
        public int Total => Substitutions + Deletions + Insertions;

        /// <summary>Word error rate: edits per reference token. Empty reference => 0 when the
        /// hypothesis is also empty, else 1 (everything is an insertion error).</summary>
        public double Rate => ReferenceLength == 0 ? (Total == 0 ? 0.0 : 1.0) : (double)Total / ReferenceLength;

        public static EditCounts operator +(EditCounts a, EditCounts b) => new(
            a.Substitutions + b.Substitutions, a.Deletions + b.Deletions,
            a.Insertions + b.Insertions, a.ReferenceLength + b.ReferenceLength);
    }

    /// <summary>Levenshtein over token sequences, classified into substitutions/deletions/
    /// insertions by backtracking. Reference is the ground truth; hypothesis is the candidate.</summary>
    public static EditCounts Distance(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        int n = reference.Count, m = hypothesis.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        int sub = 0, del = 0, ins = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && reference[i - 1] == hypothesis[j - 1] && d[i, j] == d[i - 1, j - 1])
            {
                i--; j--; // match, no edit
            }
            else if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + 1)
            {
                sub++; i--; j--;
            }
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1)
            {
                del++; i--; // a reference token with no hypothesis match
            }
            else
            {
                ins++; j--; // a hypothesis token absent from the reference
            }
        }
        return new EditCounts(sub, del, ins, n);
    }

    /// <summary>1.0 = identical token sequences, 0.0 = completely different. Used to score
    /// candidate alignments between two transcripts' lines.</summary>
    public static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var edits = Distance(a, b).Total;
        return Math.Max(0.0, 1.0 - (double)edits / Math.Max(a.Count, b.Count));
    }

    /// <summary>Character error rate over normalized (lowercased, single-spaced word) text.</summary>
    public static EditCounts CharDistance(string reference, string hypothesis)
    {
        var r = string.Join(' ', Tokenize(reference));
        var h = string.Join(' ', Tokenize(hypothesis));
        return Distance([.. r.Select(c => c.ToString())], [.. h.Select(c => c.ToString())]);
    }
}
