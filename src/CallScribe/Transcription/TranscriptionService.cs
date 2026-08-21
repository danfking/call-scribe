using System.Diagnostics;
using Spectre.Console;
using Whisper.net.Ggml;

namespace CallScribe.Transcription;

/// <summary>The full transcribe-and-merge pipeline for a recorded WAV pair.
/// Shared by the transcribe command and the one-shot record command.</summary>
public static class TranscriptionService
{
    public static async Task<string> RunAsync(string stemPath, string? modelName, AppConfig config, CancellationToken ct)
    {
        var othersWav = $"{stemPath}.others.wav";
        var meWav = $"{stemPath}.me.wav";
        if (!File.Exists(othersWav) || !File.Exists(meWav))
        {
            throw new FileNotFoundException($"Expected both tracks: {othersWav} and {meWav}");
        }

        var model = ModelManager.ParseModel(modelName ?? config.Model);
        var quantization = model == GgmlType.LargeV3Turbo ? ModelManager.DefaultQuantization : QuantizationType.NoQuantization;
        var modelPath = await ModelManager.EnsureWhisperModelAsync(model, quantization, ct).ConfigureAwait(false);
        var vadPath = await ModelManager.EnsureVadModelAsync(ct).ConfigureAwait(false);

        using var transcriber = new TrackTranscriber(modelPath, vadPath, config.Language);

        var transcripts = new Dictionary<string, TrackTranscript>();
        foreach (var (wav, track) in new[] { (othersWav, "Others"), (meWav, "Me") })
        {
            var watch = Stopwatch.StartNew();
            TrackTranscript transcript = null!;
            await AnsiConsole.Status().StartAsync($"Transcribing {track}...", async _ =>
            {
                transcript = await transcriber.TranscribeAsync(wav, track, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var jsonPath = $"{stemPath}.{track.ToLowerInvariant()}.json";
            transcript.Save(jsonPath);
            transcripts[track] = transcript;
            AnsiConsole.MarkupLine(
                $"{track}: {transcript.Segments.Count} segments " +
                $"({transcript.Duration:F0}s audio in {watch.Elapsed.TotalSeconds:F0}s) -> {jsonPath.EscapeMarkup()}");
        }

        var stem = Path.GetFileName(stemPath);
        var transcriptPath = TranscriptMerger.Merge(stem, transcripts["Others"], transcripts["Me"], AppPaths.TranscriptsDir);
        AnsiConsole.MarkupLine($"[green]Transcript[/] -> {transcriptPath.EscapeMarkup()}");

        if (!config.KeepAudio)
        {
            File.Delete(othersWav);
            File.Delete(meWav);
            // The live caption log carries the same words as the deleted audio; keepAudio=false
            // means nothing but the merged .md remains. No-op when the recording predates the
            // live caption log or never produced one.
            File.Delete($"{stemPath}.live.jsonl");
            AnsiConsole.MarkupLine("[grey]Audio and caption log deleted (keepAudio is false).[/]");
        }

        return transcriptPath;
    }
}
