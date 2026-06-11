namespace CallScribe.Transcription;

/// <summary>Detects speaker bleed in live captions. When the user is on speakers,
/// the microphone hears the other side of the call, so the same words surface on
/// both tracks moments apart. A caption that is highly word-similar to a recent
/// caption from the other track is an echo, not independent speech.</summary>
public sealed class CrossTrackEchoFilter(TimeSpan? window = null, double similarityThreshold = 0.6)
{
    private readonly record struct Entry(string Track, HashSet<string> Tokens, DateTime At);

    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(12);
    private readonly List<Entry> _recent = [];
    private readonly Lock _lock = new();

    /// <summary>Captions shorter than this many distinct words are never treated as
    /// echoes: "Yeah." matching "Yeah." across tracks is normal conversation.</summary>
    private const int MinTokensForMatch = 3;

    public void Record(string track, string text, DateTime at)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return;
        lock (_lock)
        {
            Prune(at);
            _recent.Add(new Entry(track, tokens, at));
        }
    }

    public bool IsEchoOfOtherTrack(string track, string text, DateTime at)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return false;
        lock (_lock)
        {
            Prune(at);
            return _recent.Any(entry =>
                entry.Track != track &&
                OverlapCoefficient(tokens, entry.Tokens) >= similarityThreshold);
        }
    }

    private void Prune(DateTime now) => _recent.RemoveAll(entry => now - entry.At > _window);

    private static double OverlapCoefficient(HashSet<string> a, HashSet<string> b)
    {
        var smaller = Math.Min(a.Count, b.Count);
        if (smaller < MinTokensForMatch) return 0;
        var intersection = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)intersection / smaller;
    }

    private static HashSet<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')', '[', ']', '-'],
                StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
}
