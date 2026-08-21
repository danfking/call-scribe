using CallScribe.Transcription;
using Spectre.Console;

namespace CallScribe.Commands;

/// <summary>What a live-first stop produced: the saved transcript and the (remapped) caption
/// lines it was built from, for downstream consumers such as the coaching-profile updater.</summary>
internal sealed record LiveStopResult(
    string TranscriptPath, IReadOnlyList<(DateTime At, string Speaker, string Text)> Lines);

/// <summary>The stop-time live-first save, shared by the foreground start/listen stop and the
/// detached `record stop`: read the caption log back, apply the speaker remap, write the
/// transcript, and apply the keepAudio retention setting. Returns null when the log holds no
/// captions (an old worker, a sink failure, or pure silence); the caller then falls back to the
/// batch pass, which does its own retention.</summary>
internal static class LiveFirstStop
{
    public static LiveStopResult? TrySave(
        string stem, IReadOnlyDictionary<string, string> remap, TimeSpan? duration, AppConfig config,
        string recordingsDir, string transcriptsDir)
    {
        var captionLog = Path.Combine(recordingsDir, $"{stem}.live.jsonl");
        var lines = LiveCaptionLog.Remap(LiveCaptionLog.Read(captionLog), remap);
        if (lines.Count == 0) return null;

        var path = TranscriptMerger.MergeLive(stem, lines, transcriptsDir, duration);
        AnsiConsole.MarkupLine($"[green]Live transcript saved[/] (skipped the batch pass): {path.EscapeMarkup()}");

        // The same contract keepAudio=false has always had on the batch pass: once the
        // transcript is saved, nothing but the .md remains. The caption log goes with the
        // WAVs because it carries the same words the discarded audio does. Best-effort:
        // retention is never worth failing a saved transcript over.
        if (!config.KeepAudio)
        {
            try
            {
                File.Delete(Path.Combine(recordingsDir, $"{stem}.others.wav"));
                File.Delete(Path.Combine(recordingsDir, $"{stem}.me.wav"));
                File.Delete(captionLog);
                AnsiConsole.MarkupLine("[grey]Audio and caption log deleted (keepAudio is false).[/]");
            }
            catch { }
        }

        return new LiveStopResult(path, lines);
    }
}
