using System.CommandLine;
using System.Diagnostics;
using CallScribe.Audio;
using CallScribe.Transcription;
using NAudio.Wave;
using Spectre.Console;
using Whisper.net.Ggml;

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
        start.SetAction((parseResult, ct) => StartDetachedAsync(parseResult.GetValue(labelOption), ct));
        record.Subcommands.Add(start);

        var stopFullOption = new Option<bool>("--full")
        {
            Description = "Run the slow, high-accuracy batch transcription (whisper-large + VAD) instead of "
                          + "saving the live transcript. The batch pass is also the only path that honours "
                          + "keepAudio=false; the default keeps the WAVs for replay. `transcribe latest` "
                          + "re-runs it later if you change your mind.",
        };
        var stop = new Command("stop",
            "Stop the detached recording and save the live transcript immediately (use --full for the slow "
            + "high-accuracy batch pass)");
        stop.Options.Add(stopFullOption);
        stop.SetAction((parseResult, ct) => StopDetachedAsync(parseResult.GetValue(stopFullOption), ct));
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

    private static async Task<int> StartDetachedAsync(string? label, CancellationToken ct)
    {
        if (LiveCaptureGuard.Unavailable()) return 1;
        if (IsRecordingInProgress())
        {
            AnsiConsole.MarkupLine("[red]A recording is already in progress. Stop it first.[/]");
            return 1;
        }

        // Ensure the live caption model here, where there is a console to show a first-run
        // download's progress; the windowless worker would otherwise block on it silently.
        try
        {
            await ModelManager.EnsureWhisperModelAsync(
                    ModelManager.ParseModel(AppConfig.Load().LiveModel), QuantizationType.NoQuantization, ct)
                .ConfigureAwait(false);
        }
        catch { /* the worker degrades to capture-only recording when the model is unavailable */ }

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
        var config = AppConfig.Load();
        using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config);

        // Live captions in the worker feed {stem}.live.jsonl (tailable during the call) and the
        // live-first transcript at stop. There is no console here, so the caption engine's display
        // degrades to plain writes against a null stdout; the echo filter and deferred Me decisions
        // still run. Any failure (model missing, jsonl unwritable) degrades to capture-only, which
        // is exactly the old behaviour. Note the taps are unbounded: if the live model falls behind
        // realtime, memory grows for the call's duration (same exposure as the foreground path).
        LiveCaptionEngine? captions = null;
        LiveCaptionLog? liveLog = null;
        try
        {
            var liveModelPath = await ModelManager.EnsureWhisperModelAsync(
                    ModelManager.ParseModel(config.LiveModel), QuantizationType.NoQuantization, ct)
                .ConfigureAwait(false);
            var log = LiveCaptionLog.TryCreate(LiveCaptionLog.PathFor(stem));
            if (log != null)
            {
                captions = new LiveCaptionEngine(liveModelPath, config.LiveMeSpeechThreshold);
                captions.CaptionEmitted += log.Append;
                captions.Attach(LiveCaptionEngine.OthersLabel, "yellow", engine.OthersTrack.AddTap(), engine.OthersTrack.WaveFormat);
                captions.Attach(LiveCaptionEngine.MeLabel, "cyan", engine.MeTrack.AddTap(), engine.MeTrack.WaveFormat);
                liveLog = log;
            }
        }
        catch
        {
            captions?.Dispose();
            captions = null;
        }

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
            if (captions != null)
            {
                // Drain the final whisper flush and any deferred Me decisions so the jsonl
                // carries the whole call; best-effort, the WAVs are already safe.
                try { await captions.CompleteAsync().ConfigureAwait(false); } catch { }
                captions.Dispose();
            }
            liveLog?.Dispose();
            File.Delete(AppPaths.StopFlag);
        }
        return 0;
    }

    private static async Task<int> StopDetachedAsync(bool full, CancellationToken ct)
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
            // The worker finishes its final whisper flush plus deferred Me decisions (up to ~6 s)
            // before exiting, so give it longer than a capture-only stop would need.
            if (!worker.WaitForExit(TimeSpan.FromSeconds(30)))
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

        // Default: the worker's live captions become the transcript immediately, same as the
        // start command's live-first stop. The batch pass only runs on --full, or when the live
        // log is empty (an old worker, a sink failure, or pure silence), so a stop always yields
        // an artifact. This path never deletes the WAVs; they stay replayable.
        if (!full)
        {
            var lines = LiveCaptionLog.Read(LiveCaptionLog.PathFor(stem));
            if (lines.Count > 0)
            {
                var path = TranscriptMerger.MergeLive(stem, lines, AppPaths.TranscriptsDir, WavDuration(others));
                AnsiConsole.MarkupLine($"[green]Live transcript saved[/] (skipped the batch pass): {path.EscapeMarkup()}");
                return 0;
            }
            AnsiConsole.MarkupLine(
                "[yellow]No live captions were logged for this recording; running the batch transcription instead.[/]");
        }

        var stemPath = Path.Combine(AppPaths.RecordingsDir, stem);
        await Transcription.TranscriptionService.RunAsync(stemPath, modelName: null, AppConfig.Load(), ct)
            .ConfigureAwait(false);
        return 0;
    }

    /// <summary>The recorded length for the transcript frontmatter, from the WAV header. Null (a
    /// truncated header after a crash) lets MergeLive fall back to the caption span.</summary>
    private static TimeSpan? WavDuration(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.TotalTime;
        }
        catch
        {
            return null;
        }
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
        AnsiConsole.MarkupLine($"[grey]Live captions:[/] {LiveCaptionLog.PathFor(stem).EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[grey]Recordings:[/]   {AppPaths.RecordingsDir.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[grey]Transcripts:[/]  {AppPaths.TranscriptsDir.EscapeMarkup()}");
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
