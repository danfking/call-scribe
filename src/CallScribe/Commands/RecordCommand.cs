using System.CommandLine;
using System.Diagnostics;
using CallScribe.Audio;
using Spectre.Console;

namespace CallScribe.Commands;

public static class RecordCommand
{
    public static Command Create()
    {
        var labelOption = new Option<string?>("--label", "-l")
        {
            Description = "Label appended to the recording name, e.g. standup",
        };

        // The foreground record verb is retired: `start` (live captions + transcribe) covers it.
        // `record` is now a container for detached background recording only.
        var record = new Command("record",
            "Background (detached) recording: start it, walk away, then stop it later (it transcribes on stop)");

        var start = new Command("start", "Start a detached background recording");
        start.Options.Add(labelOption);
        start.SetAction(parseResult => StartDetached(parseResult.GetValue(labelOption)));
        record.Subcommands.Add(start);

        var stop = new Command("stop", "Stop the detached recording, finalise the files, and transcribe");
        stop.SetAction((_, ct) => StopDetachedAsync(ct));
        record.Subcommands.Add(stop);

        var status = new Command("status", "Show whether a recording is in progress");
        status.SetAction(_ => Status());
        record.Subcommands.Add(status);

        // Internal: the detached worker process re-invokes itself with this verb.
        var run = new Command("__run") { Hidden = true };
        var stemOption = new Option<string>("--stem") { Required = true };
        run.Options.Add(stemOption);
        run.SetAction((parseResult, ct) => RunDetachedWorkerAsync(parseResult.GetValue(stemOption)!, ct));
        record.Subcommands.Add(run);

        return record;
    }

    internal static string MakeStem(string? label)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        if (string.IsNullOrWhiteSpace(label)) return stamp;
        var safe = string.Join("-", label.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return $"{stamp}-{safe}";
    }

    private static int StartDetached(string? label)
    {
        if (LiveCaptureGuard.Unavailable()) return 1;
        if (IsRecordingInProgress())
        {
            AnsiConsole.MarkupLine("[red]A recording is already in progress. Stop it first.[/]");
            return 1;
        }

        Directory.CreateDirectory(AppPaths.StateDir);
        File.Delete(AppPaths.StopFlag);

        var stem = MakeStem(label);
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine own executable path.");

        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("record");
        psi.ArgumentList.Add("__run");
        psi.ArgumentList.Add("--stem");
        psi.ArgumentList.Add(stem);

        var worker = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start recording worker.");

        File.WriteAllText(AppPaths.PidFile, worker.Id.ToString());
        File.WriteAllText(AppPaths.StemFile, stem);

        AnsiConsole.MarkupLine($"[green]Recording started[/] -> {Path.Combine(AppPaths.RecordingsDir, stem).EscapeMarkup()}.*.wav");
        AnsiConsole.MarkupLine("[grey]Loopback follows the default output device; don't switch outputs mid-call.[/]");
        AnsiConsole.MarkupLine("Stop with: [bold]call-scribe record stop[/]");
        return 0;
    }

    private static async Task<int> RunDetachedWorkerAsync(string stem, CancellationToken ct)
    {
        if (LiveCaptureGuard.Unavailable()) return 1;
        using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, AppConfig.Load());
        engine.Start();
        try
        {
            while (!File.Exists(AppPaths.StopFlag) && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await engine.StopAsync().ConfigureAwait(false);
            File.Delete(AppPaths.StopFlag);
        }
        return 0;
    }

    private static async Task<int> StopDetachedAsync(CancellationToken ct)
    {
        if (!File.Exists(AppPaths.PidFile))
        {
            AnsiConsole.MarkupLine("[yellow]No recording in progress.[/]");
            return 1;
        }

        var pid = int.Parse(File.ReadAllText(AppPaths.PidFile));
        var stem = File.Exists(AppPaths.StemFile) ? File.ReadAllText(AppPaths.StemFile) : "(unknown)";

        try
        {
            using var worker = Process.GetProcessById(pid);
            File.WriteAllText(AppPaths.StopFlag, string.Empty);
            if (!worker.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                AnsiConsole.MarkupLine("[red]Worker did not stop in time; killing it. The WAVs may need repair.[/]");
                worker.Kill();
            }
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine("[yellow]Worker already exited; recording may have stopped earlier.[/]");
        }

        File.Delete(AppPaths.PidFile);
        File.Delete(AppPaths.StemFile);

        var others = Path.Combine(AppPaths.RecordingsDir, $"{stem}.others.wav");
        var me = Path.Combine(AppPaths.RecordingsDir, $"{stem}.me.wav");
        if (!File.Exists(others) || !File.Exists(me))
        {
            AnsiConsole.MarkupLine($"[yellow]Recording stopped but output not found for stem {stem.EscapeMarkup()}.[/]");
            return 0;
        }

        var sizeMb = (new FileInfo(others).Length + new FileInfo(me).Length) / 1024.0 / 1024.0;
        AnsiConsole.MarkupLine($"[green]Recording stopped[/] -> {others.EscapeMarkup()} (+ .me.wav, {sizeMb:F1} MB total)");

        // Every recording path produces a transcript; the detached worker only captured the WAVs, so
        // run the batch transcription now that the files are finalised.
        var stemPath = Path.Combine(AppPaths.RecordingsDir, stem);
        await Transcription.TranscriptionService.RunAsync(stemPath, modelName: null, AppConfig.Load(), ct)
            .ConfigureAwait(false);
        return 0;
    }

    private static int Status()
    {
        if (!IsRecordingInProgress())
        {
            AnsiConsole.MarkupLine("No recording in progress.");
            return 0;
        }

        var stem = File.Exists(AppPaths.StemFile) ? File.ReadAllText(AppPaths.StemFile) : "(unknown)";
        AnsiConsole.MarkupLine($"[green]Recording in progress[/]: {stem.EscapeMarkup()}");
        return 0;
    }

    private static bool IsRecordingInProgress()
    {
        if (!File.Exists(AppPaths.PidFile)) return false;
        if (!int.TryParse(File.ReadAllText(AppPaths.PidFile), out var pid)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
