using System.CommandLine;
using CallScribe.Audio;
using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Speaker;
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
            Description = "Small model for live captions: tiny.en, base.en, small.en. Default from config (liveModel).",
            DefaultValueFactory = _ => "",
        };
        var secondsOption = new Option<int?>("--seconds")
        {
            Description = "Stop automatically after N seconds (default: Enter stops)",
        };
        var fullOption = new Option<bool>("--full")
        {
            Description = "Run the slow, high-accuracy batch transcription after stopping (whisper-large + VAD) "
                          + "plus offline speaker diarization and interactive naming. The default saves the "
                          + "faster live transcript instead (slightly lower accuracy).",
        };
        var coachOption = new Option<bool>("--coach")
        {
            Description = "Show the realtime meeting coach panel beside the transcript (experimental).",
        };
        var speakersOption = new Option<bool>("--speakers")
        {
            Description = "Identify far-side speakers by voice: name live captions and attribute the "
                          + "transcript after the meeting (needs the speaker models; experimental).",
        };

        var command = new Command("start",
            "Record the call: live captions on screen while it runs, then Enter stops and saves the transcript "
            + "(records both tracks, transcribes, and shows captions in one step; use --full for the slow "
            + "high-accuracy batch pass)");
        // Keep the old verb working for the desktop shortcut and muscle memory; it routes to the same handler.
        command.Aliases.Add("listen");
        command.Options.Add(labelOption);
        command.Options.Add(liveModelOption);
        command.Options.Add(secondsOption);
        command.Options.Add(fullOption);
        command.Options.Add(coachOption);
        command.Options.Add(speakersOption);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(labelOption),
            parseResult.GetValue(liveModelOption)!,
            parseResult.GetValue(secondsOption),
            parseResult.GetValue(fullOption),
            parseResult.GetValue(coachOption),
            parseResult.GetValue(speakersOption),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(string? label, string liveModel, int? seconds, bool full, bool coachFlag, bool speakersFlag, CancellationToken ct)
    {
        if (LiveCaptureGuard.Unavailable()) return 1;

        var config = AppConfig.Load();
        // --speakers turns identification on for this run, including the after-meeting pass.
        if (speakersFlag) config.SpeakerIdEnabled = true;
        // Empty flag => use the configured live model (default small.en); an explicit flag overrides.
        if (string.IsNullOrWhiteSpace(liveModel)) liveModel = config.LiveModel;

        // Live model is small (~75-466 MB); make sure it's present before capture starts.
        var liveModelPath = await ModelManager.EnsureWhisperModelAsync(
            ModelManager.ParseModel(liveModel), QuantizationType.NoQuantization, ct).ConfigureAwait(false);

        var stem = RecordCommand.MakeStem(label);
        using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config);
        using var captions = new LiveCaptionEngine(liveModelPath, config.LiveMeSpeechThreshold);

        // Every emitted caption also lands in {stem}.live.jsonl so an external process can
        // tail the call as it happens. Degrades to null; the session just runs without it.
        using var liveLog = LiveCaptionLog.TryCreate(LiveCaptionLog.PathFor(stem));
        if (liveLog != null) captions.CaptionEmitted += liveLog.Append;

        // Created inside the try so a cancellation (Ctrl-C) during capture still disposes
        // their native (sherpa) and DB-pool handles via the finally.
        CoachEngine? coach = null;
        IMemoryStore? coachMemory = null;
        SpeakerIdentity? speakerId = null;
        try
        {
            // The coach watches the same caption stream the dashboard renders. Opt-in via --coach
            // or config. The live transcript itself no longer needs the coach: the jsonl caption
            // log is its source, so a plain start runs with no coach, no Postgres, and no Ollama.
            var wantCoachPanel = coachFlag || config.CoachEnabled;
            if (wantCoachPanel)
            {
                coachMemory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
                var profileStore = CoachFactory.CreateProfileStore(config);
                var (advisor, _) = CoachFactory.CreateAdvisor(config, forceStub: false, coachMemory, profileStore);
                coach = new CoachEngine(advisor, coachMemory, stem);
                captions.EnableAdvicePanel();
                coach.AdviceEmitted += a => captions.PrintAdvice(a.At, a.Colour, a.Glyph, a.Text);
                // One place maps a coach activity to its panel presentation; seed the resting
                // state so the panel shows "Listening" before the first utterance.
                static (string Text, string Colour) Present(CoachActivity activity) => activity switch
                {
                    CoachActivity.Thinking => ("◍ Thinking…", "magenta"),
                    CoachActivity.Quiet => ("○ Considered, nothing to add", "grey"),
                    _ => ("○ Listening", "grey"),
                };
                coach.ActivityChanged += activity =>
                {
                    var (text, colour) = Present(activity);
                    captions.SetCoachActivity(text, colour);
                };
                var (restText, restColour) = Present(CoachActivity.Listening);
                captions.SetCoachActivity(restText, restColour);
                captions.CaptionEmitted += coach.Observe;
            }

            // Identify far-side speakers so live captions (and the coach) get names, not just
            // "Others". Degrades to null when off or the models/native runtime are unavailable.
            speakerId = await SpeakerIdentity.TryCreateAsync(config, ct).ConfigureAwait(false);
            if (speakerId != null)
            {
                var resolver = speakerId; // non-null capture for the closure
                captions.ResolveOthersSpeaker = (samples, token) => resolver.ResolveAsync(samples, token);
                // Drop far-side bleed that lands on the mic and label the user's own voice
                // (no-op until they enroll with `coach enroll-me`).
                captions.IdentifyMeSpeaker = (samples, token) => resolver.VerifyMeAsync(samples, token);
            }
            // Live /assign-name from the dashboard: rename the speaker + persist the voiceprint.
            captions.OnAssignName = (label, name, token) =>
                speakerId is null ? Task.FromResult(false) : speakerId.AssignNameAsync(label, name, token);

            // Live /ask: answer a question about the transcript via the local model. Always wired
            // (works without --coach); degrades to a message when Ollama is not running.
            var askChat = new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive);
            captions.OnAsk = async (question, transcript, token) =>
            {
                if (!askChat.IsReachable()) return "Q&A needs Ollama running locally.";
                var prompt = TranscriptQa.BuildUserPrompt(question, transcript);
                // Headroom so a thinking model that ignores think=false still lands the answer after
                // its (stripped) reasoning instead of truncating mid-sentence.
                return await askChat
                    .CompleteAsync(config.FastModel, TranscriptQa.SystemPrompt, prompt, jsonMode: false, maxTokens: 512, token)
                    .ConfigureAwait(false);
            };

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
                // /stop or Esc on the dashboard (or Enter when output is redirected).
                await captions.WaitForStopAsync(ct).ConfigureAwait(false);
            }

            var duration = await engine.StopAsync().ConfigureAwait(false);
            await captions.CompleteAsync().ConfigureAwait(false);
            if (coach != null)
            {
                // Captions are fully emitted now; drain any advice still in flight.
                await coach.CompleteAsync().ConfigureAwait(false);
            }
            // Fold the meeting's fragmented live speaker labels now the whole recording is in. The
            // remap renames the jsonl-sourced lines in memory below, and rewrites the coach's copy
            // of the transcript (when present) so the consolidated names flow through into the
            // memory consolidation too. Best-effort: on failure the live labels stay as they were.
            IReadOnlyDictionary<string, string> remap = new Dictionary<string, string>();
            if (speakerId != null)
            {
                try
                {
                    remap = speakerId.ConsolidateSession();
                    if (remap.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[grey]Consolidated {remap.Count} fragmented speaker label(s).[/]");
                        if (coachMemory != null)
                        {
                            await coachMemory.RelabelAsync(stem, remap, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch { remap = new Dictionary<string, string>(); }
            }
            if (coachMemory != null && wantCoachPanel)
            {
                // Consolidate the meeting into durable memories for future recall. Run it on a
                // fresh token, not the session ct: a stop (Enter/Ctrl-C) cancels ct, and the
                // end-of-meeting write is exactly the work that should survive the stop. Skipped when
                // the coach panel is off (a plain listen wants speed and no Ollama dependency).
                try
                {
                    var consolidator = new MeetingConsolidator(
                        new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive), config.ReasoningModel, coachMemory);
                    var stored = await consolidator.ConsolidateAsync(stem, CancellationToken.None).ConfigureAwait(false);
                    AnsiConsole.MarkupLine($"[grey]Coach stored {stored} memories from this meeting.[/]");
                }
                catch { /* consolidation is best-effort; never block the transcript */ }
            }
            AnsiConsole.MarkupLine($"\n[green]Stopped[/] after {duration:hh\\:mm\\:ss}.");

            // Default: save the live transcript (consolidated via the remap above) as the .md
            // artifact and refine coaching profiles from it. This skips the slow whisper-large batch
            // pass, which on a long call is minutes of waiting; the benchmark (tools/TranscriptReconcile)
            // showed the live transcript stands in for it once the live model is large enough and
            // speaker labels are consolidated (use --speakers). The jsonl caption log is the source,
            // so this needs no services; the batch pass only runs when the log is empty (a sink
            // failure, or pure silence).
            if (!full)
            {
                var live = LiveCaptionLog.Remap(LiveCaptionLog.Read(LiveCaptionLog.PathFor(stem)), remap);
                if (live.Count > 0)
                {
                    var path = TranscriptMerger.MergeLive(stem, live, AppPaths.TranscriptsDir, duration);
                    AnsiConsole.MarkupLine($"[green]Live transcript saved[/] (skipped the batch pass): {path}");

                    // Keep coaching profiles evolving on the fast path. The --full path refines them
                    // from the offline-attributed transcript, but that pass does not run here, so use
                    // the consolidated live transcript instead. Best-effort, on CancellationToken.None
                    // like the other end-of-meeting work.
                    await UpdateCoachingProfilesAsync(
                        config, [.. live.Select(l => (l.Speaker, l.Text))], CancellationToken.None).ConfigureAwait(false);
                    return 0;
                }
                AnsiConsole.MarkupLine(
                    "[yellow]No live captions were logged; running the batch transcription instead.[/]");
            }

            var stemPath = Path.Combine(AppPaths.RecordingsDir, stem);
            await TranscriptionService.RunAsync(stemPath, modelName: null, config, ct).ConfigureAwait(false);

            // Authoritative after-meeting attribution: offline diarization is far more accurate
            // than the live guesser and enrolls newly-named speakers for next time.
            if (config.SpeakerIdEnabled && config.DiarizeAfterMeeting)
            {
                var attributed = await SpeakerAttributionFlow.RunAsync(stemPath, config, interactive: true, ct)
                    .ConfigureAwait(false);

                // Refine coaching profiles from the just-named transcript. This runs after the
                // interactive naming, so a person met for the first time (named here, not live) still
                // gets a profile.
                if (attributed != null)
                {
                    await UpdateCoachingProfilesAsync(config, attributed, ct).ConfigureAwait(false);
                }
            }
            return 0;
        }
        finally
        {
            coach?.Dispose();
            if (coachMemory != null) await coachMemory.DisposeAsync().ConfigureAwait(false);
            if (speakerId != null) await speakerId.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Refine the per-person coaching profiles from a transcript. Shared by the default
    /// live path (consolidated live transcript) and the --full path (offline-attributed transcript).
    /// Best-effort: no-op when profiles are disabled or Ollama is unavailable, and a model hiccup
    /// never fails the run.</summary>
    private static async Task UpdateCoachingProfilesAsync(
        AppConfig config, IReadOnlyList<(string Speaker, string Text)> lines, CancellationToken ct)
    {
        try
        {
            var updater = CoachFactory.TryCreateProfileUpdater(config);
            if (updater != null)
            {
                var n = await updater.UpdateAsync(lines, ct).ConfigureAwait(false);
                if (n > 0) AnsiConsole.MarkupLine($"[grey]Updated {n} coaching profile(s).[/]");
            }
        }
        catch { /* best-effort; never fail the run over a coaching profile */ }
    }
}
