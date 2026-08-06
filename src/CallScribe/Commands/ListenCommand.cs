using System.CommandLine;
using CallScribe.Audio;
using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Speaker;
using CallScribe.Rpg;
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
                          + "faster live transcript instead (slightly lower accuracy; needs the coach memory DB).",
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
        var rpgOption = new Option<bool>("--rpg")
        {
            Description = "Play the meeting as a co-op RPG boss fight: participants get HP/MP, decisions "
                          + "damage the boss, circular talk heals it (replaces the coach panel; experimental).",
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
        command.Options.Add(rpgOption);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(labelOption),
            parseResult.GetValue(liveModelOption)!,
            parseResult.GetValue(secondsOption),
            parseResult.GetValue(fullOption),
            parseResult.GetValue(coachOption),
            parseResult.GetValue(speakersOption),
            parseResult.GetValue(rpgOption),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(string? label, string liveModel, int? seconds, bool full, bool coachFlag, bool speakersFlag, bool rpgFlag, CancellationToken ct)
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

        // Created inside the try so a cancellation (Ctrl-C) during capture still disposes
        // their native (sherpa) and DB-pool handles via the finally. The live modules (coach, RPG)
        // are owned by the display's module host; the command disposes them via captions.
        IMemoryStore? coachMemory = null;
        SpeakerIdentity? speakerId = null;
        try
        {
            // The dashboard slot hosts one live module at a time. Build the ones this session wants,
            // register them, and pick which is on screen. RPG and the coach panel both want the
            // slot, so when both are asked for RPG is the visible one (an explicit --rpg is a clear
            // ask for this session's novelty); the coach still runs underneath for the transcript.
            var wantRpg = rpgFlag || config.RpgEnabled;
            var wantCoachPanel = (coachFlag || config.CoachEnabled) && !wantRpg;
            if (wantRpg && (coachFlag || config.CoachEnabled))
            {
                AnsiConsole.MarkupLine("[grey]RPG mode is the active panel this session; the coach still runs underneath.[/]");
            }
            // The live transcript is the default artifact; only --full (batch pass) opts out of
            // saving it. The coach engine persists that transcript (and its memory store holds it
            // for the stop-time export), so the coach is built whenever the panel is shown or the
            // live transcript is wanted, with a stub advisor when only persistence is needed.
            var wantLiveTranscript = !full;
            string? activeModuleId = null;

            if (wantCoachPanel || wantLiveTranscript)
            {
                coachMemory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
                var profileStore = CoachFactory.CreateProfileStore(config);
                var (advisor, _) = CoachFactory.CreateAdvisor(config, forceStub: !wantCoachPanel, coachMemory, profileStore);
                var coachModule = new CoachModule(new CoachEngine(advisor, coachMemory, stem));
                captions.RegisterModule(coachModule);
                // Fed every caption: the coach persists the transcript whether or not its panel is
                // shown (its advice work is what pauses when hidden, not its persistence).
                captions.CaptionEmitted += coachModule.Observe;
                if (wantCoachPanel) activeModuleId = coachModule.Id;
            }

            if (wantRpg)
            {
                var rpgModule = new RpgModule(config.SelfSpeakerName);
                captions.RegisterModule(rpgModule);
                captions.CaptionEmitted += rpgModule.Observe;
                activeModuleId = rpgModule.Id;
            }

            captions.SetActiveModule(activeModuleId);

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
            // Captions are fully emitted now; drain any module work still in flight (advice in the
            // coach's queue, the RPG loop) before reading the persisted transcript below.
            await captions.CompleteModulesAsync().ConfigureAwait(false);
            // Fold the meeting's fragmented live speaker labels now the whole recording is in, and
            // rewrite the persisted live transcript so the consolidated names flow through into the
            // memory consolidation below (and any later read of the live transcript). Best-effort and
            // only meaningful when both speaker-id and the coach's transcript store are present.
            if (speakerId != null && coachMemory != null)
            {
                try
                {
                    var remap = speakerId.ConsolidateSession();
                    if (remap.Count > 0)
                    {
                        var relabelled = await coachMemory.RelabelAsync(stem, remap, CancellationToken.None).ConfigureAwait(false);
                        AnsiConsole.MarkupLine(
                            $"[grey]Consolidated {remap.Count} fragmented speaker label(s); relabelled {relabelled} caption(s).[/]");
                    }
                }
                catch { /* best-effort: the live labels simply stay as they were */ }
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

            // Default: save the live transcript (already consolidated above) as the .md artifact and
            // refine coaching profiles from it. This skips the slow whisper-large batch pass, which on
            // a long call is minutes of waiting; the benchmark (tools/TranscriptReconcile) showed the
            // live transcript stands in for it once the live model is large enough and speaker labels
            // are consolidated (use --speakers). Falls back to the batch pass if the coach DB that
            // holds the live transcript is unavailable.
            if (!full)
            {
                if (coachMemory != null)
                {
                    var live = await coachMemory.GetTranscriptAsync(stem, CancellationToken.None).ConfigureAwait(false);
                    var path = TranscriptMerger.MergeLive(
                        stem, [.. live.Select(l => (l.At, l.Speaker, l.Text))], AppPaths.TranscriptsDir, duration);
                    AnsiConsole.MarkupLine($"[green]Live transcript saved[/] (skipped the batch pass): {path}");

                    // Keep coaching profiles evolving on the fast path. The --full path refines them
                    // from the offline-attributed transcript, but that pass does not run here, so use
                    // the consolidated live transcript instead. Best-effort, on CancellationToken.None
                    // like the other end-of-meeting work.
                    await UpdateCoachingProfilesAsync(
                        config, [.. live.Select(l => (l.Speaker, l.Text))], CancellationToken.None).ConfigureAwait(false);
                    return 0;
                }
                // Coach DB unavailable: we cannot save the live transcript. Fall through to the batch
                // pass so the run still produces an artifact rather than promising one it cannot write.
                AnsiConsole.MarkupLine(
                    "[yellow]The live transcript needs the coach memory DB, which is unavailable; running the batch transcription instead.[/]");
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
            // Dispose the live modules (their engines) before the memory store they wrote to.
            captions.DisposeModules();
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
