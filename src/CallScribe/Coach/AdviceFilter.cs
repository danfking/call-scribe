using CallScribe.Transcription;

namespace CallScribe.Coach;

/// <summary>Suppresses repeated advice. The coach judges each utterance independently,
/// so without this the same point (often a recalled memory) resurfaces on consecutive
/// captions. A new advice that is highly word-similar to one emitted within the
/// retention window is treated as a repeat and dropped. Mirrors the token-overlap
/// approach of <see cref="CallScribe.Transcription.CrossTrackEchoFilter"/>.</summary>
public sealed class AdviceFilter(double similarityThreshold = 0.6, TimeSpan? retentionWindow = null)
{
    private readonly TimeSpan _retention = retentionWindow ?? TimeSpan.FromSeconds(90);
    private readonly List<(DateTime At, HashSet<string> Tokens)> _recent = [];
    private readonly Lock _lock = new();

    /// <summary>True if this advice is novel enough to show (and records it); false if
    /// it repeats recent advice or is empty.</summary>
    public bool ShouldEmit(string text, DateTime now)
    {
        var tokens = TokenOverlap.Tokenize(text);
        if (tokens.Count == 0) return false;

        lock (_lock)
        {
            Prune(now);
            foreach (var entry in _recent)
            {
                if (TokenOverlap.OverlapCoefficient(tokens, entry.Tokens) >= similarityThreshold) return false;
            }
            _recent.Add((now, tokens));
            return true;
        }
    }

    private void Prune(DateTime now) => _recent.RemoveAll(entry => now - entry.At > _retention);
}
