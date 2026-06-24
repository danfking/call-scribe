namespace TranscriptReconcile;

/// <summary>A single transcribed word carrying the speaker and time of the line it came from.
/// Flattening transcripts to word streams lets WER/recall/speaker/timing be measured independently
/// of how each transcript chose its line boundaries (live chunks vs final segments differ).</summary>
public readonly record struct Word(string Token, string Speaker, double TimeSec);

/// <summary>One step of a word-level alignment: match/substitution (both set), deletion
/// (hypothesis null, a reference word the hypothesis missed), or insertion (reference null).</summary>
public readonly record struct WordPair(Word? Reference, Word? Hypothesis);

public static class WordStream
{
    public static IReadOnlyList<Word> Flatten(IReadOnlyList<Utterance> utterances)
    {
        var words = new List<Word>();
        foreach (var u in utterances)
        {
            foreach (var token in WordError.Tokenize(u.Text))
            {
                words.Add(new Word(token, u.Speaker, u.StartSec));
            }
        }
        return words;
    }

    /// <summary>Token-level Levenshtein alignment with backtrack, carrying each word's speaker/time
    /// so downstream metrics (WER, word recall/precision, speaker agreement, timing) all derive from
    /// one segmentation-independent alignment.</summary>
    public static IReadOnlyList<WordPair> Align(IReadOnlyList<Word> reference, IReadOnlyList<Word> hypothesis)
    {
        int n = reference.Count, m = hypothesis.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = reference[i - 1].Token == hypothesis[j - 1].Token ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        var pairs = new List<WordPair>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && reference[i - 1].Token == hypothesis[j - 1].Token && d[i, j] == d[i - 1, j - 1])
            {
                pairs.Add(new WordPair(reference[i - 1], hypothesis[j - 1])); i--; j--; // match
            }
            else if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + 1)
            {
                pairs.Add(new WordPair(reference[i - 1], hypothesis[j - 1])); i--; j--; // substitution
            }
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1)
            {
                pairs.Add(new WordPair(reference[i - 1], null)); i--; // deletion
            }
            else
            {
                pairs.Add(new WordPair(null, hypothesis[j - 1])); j--; // insertion
            }
        }
        pairs.Reverse();
        return pairs;
    }
}
