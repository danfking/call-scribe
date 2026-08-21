using CallScribe;
using CallScribe.Commands;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class LiveFirstStopTests
{
    private static readonly DateTime At = new(2026, 8, 20, 10, 0, 0, DateTimeKind.Local);

    private sealed class TempDirs : IDisposable
    {
        public string Recordings { get; }
        public string Transcripts { get; }

        public TempDirs()
        {
            var root = Path.Combine(Path.GetTempPath(), "call-scribe-tests", Guid.NewGuid().ToString("N"));
            Recordings = Path.Combine(root, "recordings");
            Transcripts = Path.Combine(root, "transcripts");
            Directory.CreateDirectory(Recordings);
            Directory.CreateDirectory(Transcripts);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Recordings)!, recursive: true); } catch { }
        }
    }

    private static void WriteCaptions(string recordingsDir, string stem)
    {
        using var log = LiveCaptionLog.TryCreate(Path.Combine(recordingsDir, $"{stem}.live.jsonl"));
        log!.Append(new CaptionEvent(At, "Me", "Morning."));
        log.Append(new CaptionEvent(At.AddSeconds(5), "Others", "Morning Dan.", Speaker: "Speaker 1"));
    }

    private static void WriteWavPair(string recordingsDir, string stem)
    {
        File.WriteAllBytes(Path.Combine(recordingsDir, $"{stem}.others.wav"), [0x52, 0x49, 0x46, 0x46]);
        File.WriteAllBytes(Path.Combine(recordingsDir, $"{stem}.me.wav"), [0x52, 0x49, 0x46, 0x46]);
    }

    private static readonly IReadOnlyDictionary<string, string> NoRemap = new Dictionary<string, string>();

    [Fact]
    public void TrySave_ReturnsNullWhenNoCaptionsWereLogged()
    {
        using var dirs = new TempDirs();

        var result = LiveFirstStop.TrySave(
            "2026-08-20-1000-empty", NoRemap, duration: null, new AppConfig(), dirs.Recordings, dirs.Transcripts);

        Assert.Null(result);
    }

    [Fact]
    public void TrySave_WritesTheTranscriptAndKeepsEverythingByDefault()
    {
        using var dirs = new TempDirs();
        const string stem = "2026-08-20-1000-standup";
        WriteCaptions(dirs.Recordings, stem);
        WriteWavPair(dirs.Recordings, stem);

        var result = LiveFirstStop.TrySave(
            stem, NoRemap, TimeSpan.FromMinutes(2), new AppConfig(), dirs.Recordings, dirs.Transcripts);

        Assert.NotNull(result);
        Assert.True(File.Exists(result.TranscriptPath));
        Assert.Contains("source: live", File.ReadAllText(result.TranscriptPath));
        // keepAudio defaults to true: the WAVs stay replayable and the raw caption log stays tailable.
        Assert.True(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.others.wav")));
        Assert.True(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.me.wav")));
        Assert.True(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.live.jsonl")));
    }

    [Fact]
    public void TrySave_AppliesTheSpeakerRemapToTheTranscriptAndTheReturnedLines()
    {
        using var dirs = new TempDirs();
        const string stem = "2026-08-20-1000-named";
        WriteCaptions(dirs.Recordings, stem);

        var result = LiveFirstStop.TrySave(
            stem, new Dictionary<string, string> { ["Speaker 1"] = "Priya" }, duration: null,
            new AppConfig(), dirs.Recordings, dirs.Transcripts);

        Assert.NotNull(result);
        Assert.Contains("**Priya**", File.ReadAllText(result.TranscriptPath));
        Assert.Contains(result.Lines, l => l.Speaker == "Priya");
        Assert.DoesNotContain(result.Lines, l => l.Speaker == "Speaker 1");
    }

    [Fact]
    public void TrySave_DeletesTheAudioAndCaptionLogWhenKeepAudioIsFalse()
    {
        using var dirs = new TempDirs();
        const string stem = "2026-08-20-1000-confidential";
        WriteCaptions(dirs.Recordings, stem);
        WriteWavPair(dirs.Recordings, stem);

        var result = LiveFirstStop.TrySave(
            stem, NoRemap, duration: null,
            new AppConfig { KeepAudio = false }, dirs.Recordings, dirs.Transcripts);

        // Same contract keepAudio=false has always had on the batch pass: after a successful
        // transcript, nothing but the .md remains.
        Assert.NotNull(result);
        Assert.True(File.Exists(result.TranscriptPath));
        Assert.False(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.others.wav")));
        Assert.False(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.me.wav")));
        Assert.False(File.Exists(Path.Combine(dirs.Recordings, $"{stem}.live.jsonl")));
    }
}
