using System.CommandLine;
using CallScribe.Audio;
using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Transcription;
using Spectre.Console;
using Whisper.net.Ggml;

namespace CallScribe.Commands;

public static class ListenCommand
{
    public static Command Create()
    {
        var labelOption = new Option<string?>("--label", "-l")
        {
            Description = "Label appended to the recording name, e.g. standup",
        };
        var liveModelOption = new Option<string>("--live-model")
        {
            Description = "Small model for live captions: base.en (default), tiny.en, small.en",
            DefaultValueFactory = _ => "base.en",
        };
        var secondsOption = new Option<int?>("--seconds")
        {
            Description = "Stop automatically after N seconds (default: Enter stops)",
        };
        var noTranscribeOption = new Option<bool>("--no-transcribe")
        {
            Description = "Skip the full-quality transcription after stopping",
        };
        var aecOption = new Option<bool>("--aec")
        {
            Description = "Cancel far-side speaker bleed from the mic with the Windows Voice Capture DSP "
                          + "(for speaker use; unnecessary on headphones). The Me track becomes 16 kHz mono.",
        };
        var aesOption = new Option<int>("--aes")
        {
            Description = "AEC residual suppressor level 0-2 (default 0 = plain AEC, never clips your voice). "
                          + "Higher cancels more far-side bleed but can clip your own voice during double-talk. "
                          + "Only used with --aec.",
            DefaultValueFactory = _ => 0,
        };
        var coachOption = new Option<bool>("--coach")
        {
            Description = "Show the realtime meeting coach panel beside the transcript (experimental).",
        };

        var command = new Command("listen",
            "Record with live captions on screen; Enter stops, then the full-quality transcription runs");
        command.Options.Add(labelOption);
        command.Options.Add(liveModelOption);
        command.Options.Add(secondsOption);
        command.Options.Add(noTranscribeOption);
        command.Options.Add(aecOption);
        command.Options.Add(aesOption);
        command.Options.Add(coachOption);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(labelOption),
            parseResult.GetValue(liveModelOption)!,
            parseResult.GetValue(secondsOption),
            parseResult.GetValue(noTranscribeOption),
            parseResult.GetValue(aecOption),
            parseResult.GetValue(aesOption),
            parseResult.GetValue(coachOption),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(string? label, string liveModel, int? seconds, bool noTranscribe, bool aec, int aes, bool coachFlag, CancellationToken ct)
    {
        var config = AppConfig.Load();

        // Live model is small (~75-466 MB); make sure it's present before capture starts.
        var liveModelPath = await ModelManager.EnsureWhisperModelAsync(
            ModelManager.ParseModel(liveModel), QuantizationType.NoQuantization, ct).ConfigureAwait(false);

        var stem = RecordCommand.MakeStem(label);
        using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config, aecMic: aec, aecSuppressionLevel: aes);
        using var captions = new LiveCaptionEngine(liveModelPath);

        // The coach watches the same caption stream the dashboard renders. Opt-in via
        // --coach or config; the stub advisor needs no models, so this works offline.
        CoachEngine? coach = null;
        IMemoryStore? coachMemory = null;
        if (coachFlag || config.CoachEnabled)
        {
            coachMemory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
            var (advisor, _) = CoachFactory.CreateAdvisor(config, forceStub: false, coachMemory);
            captions.EnableAdvicePanel();
            coach = new CoachEngine(advisor, coachMemory, stem);
            coach.AdviceEmitted += a => captions.PrintAdvice(a.At, a.Colour, a.Glyph, a.Text);
            captions.CaptionEmitted += coach.Observe;
        }

        // The dashboard shows the live state; it starts when the first track attaches.
        captions.ConfigureDisplay(liveModel);
        captions.Attach(LiveCaptionEngine.OthersLabel, "yellow", engine.OthersTrack.AddTap(), engine.OthersTrack.WaveFormat);
        captions.Attach(LiveCaptionEngine.MeLabel, "cyan", engine.MeTrack.AddTap(), engine.MeTrack.WaveFormat);

        engine.Start();

        if (seconds is int s)
        {
            await Task.Delay(TimeSpan.FromSeconds(s), ct).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => Console.ReadLine(), ct).ConfigureAwait(false);
        }

        var duration = await engine.StopAsync().ConfigureAwait(false);
        await captions.CompleteAsync().ConfigureAwait(false);
        if (coach != null)
        {
            // Captions are fully emitted now; drain any advice still in flight.
            await coach.CompleteAsync().ConfigureAwait(false);
            coach.Dispose();
        }
        if (coachMemory != null)
        {
            // Consolidate the meeting into durable memories for future recall.
            try
            {
                var consolidator = new MeetingConsolidator(
                    new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive), config.ReasoningModel, coachMemory);
                var stored = await consolidator.ConsolidateAsync(stem, ct).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[grey]Coach stored {stored} memories from this meeting.[/]");
            }
            catch { /* consolidation is best-effort; never block the transcript */ }
            await coachMemory.DisposeAsync().ConfigureAwait(false);
        }
        AnsiConsole.MarkupLine($"\n[green]Stopped[/] after {duration:hh\\:mm\\:ss}.");

        if (noTranscribe) return 0;

        var stemPath = Path.Combine(AppPaths.RecordingsDir, stem);
        await TranscriptionService.RunAsync(stemPath, modelName: null, config, ct).ConfigureAwait(false);
        return 0;
    }
}
