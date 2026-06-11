using System.CommandLine;
using System.Diagnostics;
using CallScribe.Transcription;
using Spectre.Console;
using Whisper.net.Ggml;

namespace CallScribe.Commands;

public static class TranscribeCommand
{
    public static Command Create()
    {
        var targetArgument = new Argument<string>("target")
        {
            Description = "Recording stem, path to a .others.wav/.me.wav pair, or 'latest'",
            DefaultValueFactory = _ => "latest",
        };
        var modelOption = new Option<string?>("--model", "-m")
        {
            Description = "Whisper model: large-v3-turbo (default), large-v3, medium, small, base, tiny",
        };

        var command = new Command("transcribe", "Transcribe a recording to per-track JSON and a merged markdown transcript");
        command.Arguments.Add(targetArgument);
        command.Options.Add(modelOption);
        command.SetAction((parseResult, ct) =>
            RunAsync(parseResult.GetValue(targetArgument)!, parseResult.GetValue(modelOption), ct));
        return command;
    }

    private static async Task<int> RunAsync(string target, string? modelName, CancellationToken ct)
    {
        var stemPath = ResolveStem(target);
        if (stemPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No recording found for '{target.EscapeMarkup()}'.[/]");
            return 1;
        }

        var othersWav = $"{stemPath}.others.wav";
        var meWav = $"{stemPath}.me.wav";
        if (!File.Exists(othersWav) || !File.Exists(meWav))
        {
            AnsiConsole.MarkupLine($"[red]Expected both tracks: {othersWav.EscapeMarkup()} and .me.wav[/]");
            return 1;
        }

        var model = modelName is null ? ModelManager.DefaultModel : ModelManager.ParseModel(modelName);
        var quantization = model == GgmlType.LargeV3Turbo ? ModelManager.DefaultQuantization : QuantizationType.NoQuantization;
        var modelPath = await ModelManager.EnsureWhisperModelAsync(model, quantization, ct).ConfigureAwait(false);
        var vadPath = await ModelManager.EnsureVadModelAsync(ct).ConfigureAwait(false);

        using var transcriber = new TrackTranscriber(modelPath, vadPath);

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
        return 0;
    }

    /// <summary>Resolve 'latest', a bare stem, or a path to one of the pair's files
    /// into the shared stem path (directory + stem, no extension).</summary>
    private static string? ResolveStem(string target)
    {
        if (target == "latest")
        {
            var newest = new DirectoryInfo(AppPaths.RecordingsDir)
                .EnumerateFiles("*.others.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? null : StripTrackSuffix(newest.FullName);
        }

        if (File.Exists(target)) return StripTrackSuffix(Path.GetFullPath(target));

        var inRecordings = Path.Combine(AppPaths.RecordingsDir, target);
        if (File.Exists($"{inRecordings}.others.wav")) return inRecordings;
        if (File.Exists($"{target}.others.wav")) return Path.GetFullPath(target);

        return null;
    }

    private static string StripTrackSuffix(string path)
    {
        foreach (var suffix in new[] { ".others.wav", ".me.wav" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length];
            }
        }
        return path;
    }
}
