using System.Text.Json;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LiveCaptionLogTests
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "call-scribe-tests", Guid.NewGuid().ToString("N"));

    private static string NewLogPath() => Path.Combine(TempDir, $"{Guid.NewGuid():N}.live.jsonl");

    [Fact]
    public void Append_WritesOneJsonObjectPerLine()
    {
        var path = NewLogPath();
        var at = new DateTime(2026, 8, 20, 14, 3, 5, DateTimeKind.Local).AddTicks(1234567);
        using (var log = LiveCaptionLog.TryCreate(path))
        {
            Assert.NotNull(log);
            log.Append(new CaptionEvent(at, "Me", "Morning everyone."));
            log.Append(new CaptionEvent(at.AddSeconds(4), "Others", "Morning Dan.", Speaker: "Kiel"));
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Me", first.RootElement.GetProperty("channel").GetString());
        // No resolved speaker: falls back to the channel label, like CaptionEvent.SpeakerName.
        Assert.Equal("Me", first.RootElement.GetProperty("speaker").GetString());
        Assert.Equal("Morning everyone.", first.RootElement.GetProperty("text").GetString());
        Assert.Equal(at, DateTime.Parse(first.RootElement.GetProperty("at").GetString()!));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Others", second.RootElement.GetProperty("channel").GetString());
        Assert.Equal("Kiel", second.RootElement.GetProperty("speaker").GetString());
    }

    [Fact]
    public void Read_RoundTripsWhatWasAppended()
    {
        var path = NewLogPath();
        var at = new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Local);
        using (var log = LiveCaptionLog.TryCreate(path))
        {
            log!.Append(new CaptionEvent(at, "Me", "A \"quoted\" line\nwith a newline."));
            log.Append(new CaptionEvent(at.AddSeconds(2), "Others", "Reply.", Speaker: "Priya"));
        }

        var lines = LiveCaptionLog.Read(path);

        Assert.Equal(2, lines.Count);
        Assert.Equal((at, "Me", "A \"quoted\" line\nwith a newline."), lines[0]);
        Assert.Equal((at.AddSeconds(2), "Priya", "Reply."), lines[1]);
    }

    [Fact]
    public void Read_SkipsMalformedLines()
    {
        var path = NewLogPath();
        using (var log = LiveCaptionLog.TryCreate(path))
        {
            log!.Append(new CaptionEvent(DateTime.Now, "Me", "Survives."));
        }
        // A killed worker can leave a partial trailing line; Read must not choke on it.
        File.AppendAllText(path, "{\"at\":\"2026-08-20T14:0");

        var lines = LiveCaptionLog.Read(path);

        Assert.Single(lines);
        Assert.Equal("Survives.", lines[0].Text);
    }

    [Fact]
    public void Read_ReturnsEmptyForMissingFile()
    {
        Assert.Empty(LiveCaptionLog.Read(Path.Combine(TempDir, "no-such.live.jsonl")));
    }

    [Fact]
    public async Task Append_FromConcurrentTasksKeepsLinesIntact()
    {
        var path = NewLogPath();
        const int tasks = 4, perTask = 25;
        using (var log = LiveCaptionLog.TryCreate(path))
        {
            await Task.WhenAll(Enumerable.Range(0, tasks).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < perTask; i++)
                {
                    log!.Append(new CaptionEvent(DateTime.Now, "Others", $"task {t} line {i}"));
                }
            })));
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(tasks * perTask, lines.Length);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose()); // every line is intact JSON
    }

    [Fact]
    public void TryCreate_TruncatesAStaleLogFromAPreviousRun()
    {
        // Stems have minute resolution, so a false start restarted within the same minute
        // reuses the path. The WAVs are truncated by their writer; the caption log must be
        // too, or the stop-time transcript replays the previous call's dialogue.
        var path = NewLogPath();
        using (var first = LiveCaptionLog.TryCreate(path))
        {
            first!.Append(new CaptionEvent(DateTime.Now, "Others", "From the earlier false start."));
        }

        using (var second = LiveCaptionLog.TryCreate(path))
        {
            second!.Append(new CaptionEvent(DateTime.Now, "Me", "The real call."));
        }

        var lines = LiveCaptionLog.Read(path);
        Assert.Single(lines);
        Assert.Equal("The real call.", lines[0].Text);
    }

    [Fact]
    public void TryCreate_ReturnsNullWhenThePathIsUnwritable()
    {
        // A path whose "directory" is an existing file cannot be created.
        Directory.CreateDirectory(TempDir);
        var blocker = Path.Combine(TempDir, "blocker.txt");
        File.WriteAllText(blocker, "in the way");

        Assert.Null(LiveCaptionLog.TryCreate(Path.Combine(blocker, "x.live.jsonl")));
    }

    [Fact]
    public void Append_IsReadableWhileTheWriterHoldsTheFile()
    {
        // Pins the tailing contract: an external process must be able to read
        // flushed lines mid-call (Get-Content -Wait style).
        var path = NewLogPath();
        using var log = LiveCaptionLog.TryCreate(path);
        log!.Append(new CaptionEvent(DateTime.Now, "Me", "Visible mid-call."));

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        Assert.Contains("Visible mid-call.", text.ReadToEnd());
    }

    [Fact]
    public void Remap_RenamesConsolidatedSpeakersAndLeavesTheRestAlone()
    {
        // The stop-time speaker consolidation folds fragmented live labels ("Speaker 2" turned
        // out to be Priya); the jsonl stays raw, so the remap is applied in memory on the way
        // to MergeLive.
        var at = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Local);
        IReadOnlyList<(DateTime At, string Speaker, string Text)> lines =
        [
            (at, "Me", "Hi."),
            (at.AddSeconds(3), "Speaker 1", "Hello."),
            (at.AddSeconds(6), "Speaker 2", "Also hello."),
        ];

        var remapped = LiveCaptionLog.Remap(lines, new Dictionary<string, string>
        {
            ["Speaker 1"] = "Priya",
            ["Speaker 2"] = "Priya",
        });

        Assert.Equal((at, "Me", "Hi."), remapped[0]);
        Assert.Equal((at.AddSeconds(3), "Priya", "Hello."), remapped[1]);
        Assert.Equal((at.AddSeconds(6), "Priya", "Also hello."), remapped[2]);
    }

    [Fact]
    public void PathFor_PutsTheLogNextToTheRecordings()
    {
        Assert.EndsWith("2026-08-20-0930-standup.live.jsonl", LiveCaptionLog.PathFor("2026-08-20-0930-standup"));
        Assert.StartsWith(CallScribe.AppPaths.RecordingsDir, LiveCaptionLog.PathFor("2026-08-20-0930-standup"));
    }
}
