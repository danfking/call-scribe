namespace CallScribe.Transcription;

/// <summary>Detects speaker bleed in live captions. When the user is on speakers,
/// the microphone hears the other side of the call, so the same words surface on
/// both tracks covering the same stretch of wall-clock time. A caption that is
/// highly word-similar to a caption from the other track with an overlapping
/// audio span is an echo, not independent speech.</summary>
public sealed class CrossTrackEchoFilter(double similarityThreshold = 0.6)
{
    private readonly record struct Entry(string Track, HashSet<string> Tokens, DateTime SpanStart, DateTime SpanEnd);

    /// <summary>Spans are compared with this much slack on each side: chunk
    /// boundaries differ between tracks for the same underlying speech.</summary>
    private static readonly TimeSpan SpanSlack = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(60);

    /// <summary>Captions shorter than this many distinct words are never treated as
    /// echoes: "Yeah." matching "Yeah." across tracks is normal conversation.</summary>
    private const int MinTokensForMatch = 3;

    private readonly List<Entry> _recent = [];
    private readonly Lock _lock = new();

    public void Record(string track, string text, DateTime spanStart, DateTime spanEnd)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return;
        lock (_lock)
        {
            Prune(spanEnd);
            _recent.Add(new Entry(track, tokens, spanStart, spanEnd));
        }
    }

    public bool IsEchoOfOtherTrack(string track, string text, DateTime spanStart, DateTime spanEnd)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return false;
        lock (_lock)
        {
            Prune(spanEnd);
            return _recent.Any(entry =>
                entry.Track != track &&
                SpansOverlap(entry, spanStart, spanEnd) &&
                OverlapCoefficient(tokens, entry.Tokens) >= similarityThreshold);
        }
    }

    private static bool SpansOverlap(Entry entry, DateTime spanStart, DateTime spanEnd) =>
        entry.SpanStart - SpanSlack < spanEnd && spanStart < entry.SpanEnd + SpanSlack;

    private void Prune(DateTime now) => _recent.RemoveAll(entry => now - entry.SpanEnd > RetentionWindow);

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
