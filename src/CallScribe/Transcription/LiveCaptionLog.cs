using System.Text.Json;

namespace CallScribe.Transcription;

/// <summary>Appends every emitted live caption to a {stem}.live.jsonl file next to the WAVs, one
/// JSON object per line, flushed per line so an external process can tail it mid-call. This is the
/// file-based caption stream: unlike the coach's Postgres store it needs no services at all, and at
/// stop time it is the source for the live transcript. Kept after finalisation (it is the cheap,
/// replayable raw stream, like the batch pass's per-track .json files).</summary>
public sealed class LiveCaptionLog : IDisposable
{
    // CaptionEmitted fires from the Others track worker and from deferred Me-decision tasks,
    // so appends must be serialised.
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;

    private LiveCaptionLog(StreamWriter writer) => _writer = writer;

    public static string PathFor(string stem) => Path.Combine(AppPaths.RecordingsDir, $"{stem}.live.jsonl");

    /// <summary>Open the log for appending, or null when the file cannot be opened (the recording
    /// then simply runs without a live caption file, per the degrade-to-null convention).</summary>
    public static LiveCaptionLog? TryCreate(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // FileShare.ReadWrite is the tailing contract: Get-Content -Wait (and Read below)
            // must be able to open the file while the recording holds it.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new LiveCaptionLog(new StreamWriter(stream));
        }
        catch
        {
            return null;
        }
    }

    public void Append(CaptionEvent e)
    {
        var json = JsonSerializer.Serialize(new Line(e.At, e.Label, e.SpeakerName, e.Caption), JsonOptions);
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(json);
                _writer.Flush();
            }
            catch { /* a caption line is never worth failing the recording over */ }
        }
    }

    /// <summary>Read a log back as the tuple list <see cref="TranscriptMerger.MergeLive"/> takes.
    /// Malformed lines are skipped: a killed worker can leave a partial trailing line, and losing
    /// one caption beats losing the transcript.</summary>
    public static IReadOnlyList<(DateTime At, string Speaker, string Text)> Read(string path)
    {
        if (!File.Exists(path)) return [];
        var lines = new List<(DateTime, string, string)>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            try
            {
                var line = JsonSerializer.Deserialize<Line>(raw, JsonOptions);
                if (line is { Speaker: not null, Text: not null })
                {
                    lines.Add((line.At, line.Speaker, line.Text));
                }
            }
            catch (JsonException) { }
        }
        return lines;
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>One caption line. Channel is the audio track ("Me"/"Others"); Speaker is the best
    /// name known at emit time (the resolved person, else the channel label).</summary>
    private sealed record Line(DateTime At, string Channel, string Speaker, string Text);
}
