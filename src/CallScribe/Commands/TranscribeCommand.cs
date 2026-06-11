using System.CommandLine;
using CallScribe.Transcription;
using Spectre.Console;

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

        await TranscriptionService.RunAsync(stemPath, modelName, AppConfig.Load(), ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Resolve 'latest', a bare stem, or a path to one of the pair's files
    /// into the shared stem path (directory + stem, no extension).</summary>
    private static string? ResolveStem(string target)
    {
        if (target == "latest")
        {
            var recordings = new DirectoryInfo(AppPaths.RecordingsDir);
            if (!recordings.Exists) return null;
            var newest = recordings
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
