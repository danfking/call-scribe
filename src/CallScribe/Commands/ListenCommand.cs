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
        var noTranscribeOption = new Option<bool>("--no-transcribe")
        {
            Description = "Skip the full-quality transcription after stopping",
        };
        var liveOnlyOption = new Option<bool>("--live-only")
        {
            Description = "Skip the slow batch transcription and save the live transcript instead "
                          + "(faster; slightly lower accuracy). Best with --speakers so the live "
                          + "speaker labels are consolidated first. Needs the coach memory DB.",
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
        var speakersOption = new Option<bool>("--speakers")
        {
            Description = "Identify far-side speakers by voice: name live captions and attribute the "
                          + "transcript after the meeting (needs the speaker models; experimental).",
        };

        var command = new Command("listen",
            "Record with live captions on screen; Enter stops, then the full-quality transcription runs");
        command.Options.Add(labelOption);
        command.Options.Add(liveModelOption);
        command.Options.Add(secondsOption);
        command.Options.Add(noTranscribeOption);
        command.Options.Add(liveOnlyOption);
        command.Options.Add(aecOption);
        command.Options.Add(aesOption);
        command.Options.Add(coachOption);
        command.Options.Add(speakersOption);
        command.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(labelOption),
            parseResult.GetValue(liveModelOption)!,
            parseResult.GetValue(secondsOption),
            parseResult.GetValue(noTranscribeOption),
            parseResult.GetValue(liveOnlyOption),
            parseResult.GetValue(aecOption),
            parseResult.GetValue(aesOption),
            parseResult.GetValue(coachOption),
            parseResult.GetValue(speakersOption),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(string? label, string liveModel, int? seconds, bool noTranscribe, bool liveOnly, bool aec, int aes, bool coachFlag, bool speakersFlag, CancellationToken ct)
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
        using var engine = new CaptureEngine(stem, AppPaths.RecordingsDir, config, aecMic: aec, aecSuppressionLevel: aes);
        using var captions = new LiveCaptionEngine(liveModelPath, config.LiveMeSpeechThreshold);

        // Created inside the try so a cancellation (Ctrl-C) during capture still disposes
        // their native (sherpa) and DB-pool handles via the finally.
        CoachEngine? coach = null;
        IMemoryStore? coachMemory = null;
        SpeakerIdentity? speakerId = null;
        try
        {
            // The coach watches the same caption stream the dashboard renders. Opt-in via --coach
            // or config; the stub advisor needs no models, so this works offline. --live-only also
            // needs the coach's transcript store (that is where the live transcript is persisted for
            // the stop-time export), but not the advice panel, so it runs the coach with a stub
            // advisor (no Ollama, no panel) purely to persist segments.
            var wantCoachPanel = coachFlag || config.CoachEnabled;
            if (wantCoachPanel || liveOnly)
            {
                coachMemory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
                var profileStore = CoachFactory.CreateProfileStore(config);
                var (advisor, _) = CoachFactory.CreateAdvisor(config, forceStub: !wantCoachPanel, coachMemory, profileStore);
                coach = new CoachEngine(advisor, coachMemory, stem);
                if (wantCoachPanel)
                {
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
                }
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
                // end-of-meeting write is exactly the work that should survive the stop. Skipped on a
                // bare --live-only run, which wants speed and no Ollama dependency.
                try
                {
                    var consolidator = new MeetingConsolidator(
                        new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive), config.ReasoningModel, coachMemory);
                    var stored = await consolidator.ConsolidateAsync(stem, CancellationToken.None).ConfigureAwait(false);
                    AnsiConsole.MarkupLine($"[grey]Coach stored {stored} memories from this meeting.[/]");
                }
                catch { /* consolidation is best-effort; never block the transcript */ }
            }
            if (coachMemory != null && wantCoachPanel && config.CoachingProfilesEnabled && config.SpeakerIdEnabled)
            {
                // Refine each named person's coaching profile from this meeting. Gated on speaker-id:
                // without it the far side is never named, so there is nobody to profile and no reason
                // to round-trip the DB. Runs after the live relabel above so it sees the consolidated
                // live names, and on a fresh token like the consolidation: a stop must not abort the write.
                try
                {
                    var updater = CoachFactory.TryCreateProfileUpdater(config, coachMemory);
                    if (updater != null)
                    {
                        var updatedProfiles = await updater.UpdateAsync(stem, CancellationToken.None).ConfigureAwait(false);
                        if (updatedProfiles > 0)
                        {
                            AnsiConsole.MarkupLine($"[grey]Updated {updatedProfiles} coaching profile(s).[/]");
                        }
                    }
                }
                catch { /* best-effort; never block the transcript */ }
            }
            AnsiConsole.MarkupLine($"\n[green]Stopped[/] after {duration:hh\\:mm\\:ss}.");

            // Live-only: skip the slow batch transcription and save the live transcript (already
            // consolidated above) as the .md artifact instead. Falls back to the batch pass if the
            // coach DB that holds the live transcript is unavailable.
            if (liveOnly)
            {
                if (coachMemory != null)
                {
                    var live = await coachMemory.GetTranscriptAsync(stem, CancellationToken.None).ConfigureAwait(false);
                    var path = TranscriptMerger.MergeLive(
                        stem, [.. live.Select(l => (l.At, l.Speaker, l.Text))], AppPaths.TranscriptsDir, duration);
                    AnsiConsole.MarkupLine($"[green]Live transcript saved[/] (skipped the batch pass): {path}");
                    return 0;
                }
                // Coach DB unavailable: we cannot save the live transcript. Fall through to the normal
                // flow below, which runs the batch pass (or honours --no-transcribe) so the message
                // never promises an artifact that the following lines would skip.
                AnsiConsole.MarkupLine(
                    "[yellow]--live-only needs the coach memory DB, which is unavailable; cannot save the live transcript.[/]");
            }

            if (noTranscribe) return 0;

            var stemPath = Path.Combine(AppPaths.RecordingsDir, stem);
            await TranscriptionService.RunAsync(stemPath, modelName: null, config, ct).ConfigureAwait(false);

            // Authoritative after-meeting attribution: offline diarization is far more accurate
            // than the live guesser and enrolls newly-named speakers for next time.
            if (config.SpeakerIdEnabled && config.DiarizeAfterMeeting)
            {
                await SpeakerAttributionFlow.RunAsync(stemPath, config, interactive: true, ct).ConfigureAwait(false);
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
}
