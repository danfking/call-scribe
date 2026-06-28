using CallScribe.Coach.Llm;
using CallScribe.Coach.Speaker;
using CallScribe.Transcription;
using Spectre.Console;

namespace CallScribe.Commands;

/// <summary>The after-meeting speaker pass: offline-diarize the Others track, rewrite the
/// merged transcript with per-speaker names, and (interactively) enroll any still-unknown
/// speakers so they auto-resolve next time. Best-effort and chatty — it degrades with a
/// note when the models, recording, or database are missing rather than failing the run.</summary>
public static class SpeakerAttributionFlow
{
    /// <summary>Returns the attributed transcript as (speaker, text) lines with authoritative names,
    /// time-ordered, for a downstream consumer such as the coaching-profile updater; null when the
    /// pass is skipped (missing recording/models) or finds no speakers.</summary>
    public static async Task<IReadOnlyList<(string Speaker, string Text)>?> RunAsync(
        string stemPath, AppConfig config, bool interactive, CancellationToken ct)
    {
        var othersWav = $"{stemPath}.others.wav";
        var meWav = $"{stemPath}.me.wav";
        var othersJson = $"{stemPath}.others.json";
        var meJson = $"{stemPath}.me.json";
        if (!File.Exists(othersWav) || !File.Exists(othersJson) || !File.Exists(meJson))
        {
            AnsiConsole.MarkupLine(
                "[grey]Speaker attribution skipped: recording or transcript missing (needs keepAudio).[/]");
            return null;
        }

        var embedder = SpeakerIdentity.TryCreateEmbedder(config);
        using var diarizer = SpeakerIdentity.TryCreateDiarizer(config);
        if (embedder == null || diarizer == null)
        {
            embedder?.Dispose();
            AnsiConsole.MarkupLine(
                "[grey]Speaker attribution skipped: speaker models not installed "
                + "(run scripts/coach-pull-speaker-models.ps1).[/]");
            return null;
        }

        var voiceprints = await SpeakerIdentity
            .TryCreateVoiceprintsAsync(config, embedder.Dimensions, ct).ConfigureAwait(false);
        try
        {
            DiarizationResult? result = null;
            await AnsiConsole.Status().StartAsync("Identifying speakers...", async _ =>
            {
                result = await OfflineDiarization
                    .AttributeAsync(othersWav, config, embedder, diarizer, voiceprints, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (result == null)
            {
                AnsiConsole.MarkupLine("[grey]No distinct speakers identified.[/]");
                return null;
            }

            // Pre-fill the prompts from how speakers introduced themselves ("I'm Sammy").
            var others = TrackTranscript.Load(othersJson);
            var suggestions = await SuggestNamesAsync(others, result, config, ct).ConfigureAwait(false);

            // Naming needs a real console to read keys. When stdin is redirected (piped, headless,
            // a cron/CI run), skip the prompts and leave clusters as their auto-resolved or
            // "Speaker N" names, rather than letting AnsiConsole.Prompt throw "non-interactive mode".
            if (interactive && voiceprints != null && !Console.IsInputRedirected)
            {
                // The full-quality transcription above can take minutes; any Enter presses
                // during it sit buffered in the console and would instantly auto-skip the
                // first naming prompt. Drain them so the prompt actually waits for input.
                DrainInput();

                // Diarization can over-split one voice into several clusters that the resolver
                // merged under the same "Speaker N" name; prompt once per distinct name and
                // apply it to every cluster sharing it (so we don't ask repeatedly, and the
                // transcript is named consistently).
                foreach (var sameName in result.Clusters.Where(c => !c.Enrolled).GroupBy(c => c.Name).ToList())
                {
                    var suggested = suggestions.GetValueOrDefault(sameName.Key);
                    var prompt = new TextPrompt<string>(
                        $"Name for [yellow]{sameName.Key}[/] (Enter to {(suggested != null ? "accept" : "skip")}):")
                        .AllowEmpty();
                    if (suggested != null) prompt.DefaultValue(suggested);

                    var name = AnsiConsole.Prompt(prompt);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    foreach (var cluster in sameName)
                    {
                        await voiceprints.EnrollAsync(name.Trim(), cluster.MeanEmbedding, ct).ConfigureAwait(false);
                        result.Rename(cluster.Index, name.Trim());
                    }
                    AnsiConsole.MarkupLine($"[green]Enrolled[/] {name.Trim().EscapeMarkup()} for next time.");
                }
            }

            // The batch pass transcribes the raw mic, which on speakers contains far-side
            // bleed; drop the Me segments that aren't the enrolled self voice (same check the
            // live path uses) so the saved transcript matches what was shown on screen.
            var me = TrackTranscript.Load(meJson);
            var (meFiltered, meLabel, dropped) =
                await FilterMeBleedAsync(me, meWav, config, embedder, voiceprints, ct).ConfigureAwait(false);
            if (dropped > 0)
            {
                AnsiConsole.MarkupLine($"[grey]Dropped {dropped} far-side bleed segment(s) from the Me track.[/]");
            }

            // Rewrite the merged transcript with the resolved names.
            var stem = Path.GetFileName(stemPath);
            var path = TranscriptMerger.Merge(stem, others, meFiltered, AppPaths.TranscriptsDir, result.SpeakerFor, meLabel);

            var names = string.Join(", ", result.Clusters.Select(c => c.Name));
            AnsiConsole.MarkupLine($"[green]Speaker-attributed transcript[/] -> {path.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[grey]Far-side speakers: {names.EscapeMarkup()}[/]");

            // The attributed transcript (authoritative names), time-ordered, for any downstream
            // consumer such as the coaching-profile updater.
            var attributed = new List<(double Start, string Speaker, string Text)>();
            foreach (var s in others.Segments)
            {
                if (!string.IsNullOrWhiteSpace(s.Text)) attributed.Add((s.Start, result.SpeakerFor(s), s.Text));
            }
            foreach (var s in meFiltered.Segments)
            {
                if (!string.IsNullOrWhiteSpace(s.Text)) attributed.Add((s.Start, meLabel, s.Text));
            }
            attributed.Sort((a, b) => a.Start.CompareTo(b.Start));
            return attributed.Select(a => (Speaker: a.Speaker, Text: a.Text)).ToList();
        }
        finally
        {
            embedder.Dispose();
            if (voiceprints != null) await voiceprints.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drop Me-track segments that are far-side bleed (not the enrolled self voice),
    /// using the same voiceprint check as the live path, and label the kept ones with the self
    /// name. No-op (returns the track unchanged, labelled "Me") when self isn't enrolled, the
    /// voiceprint store is unavailable, or the mic WAV is missing. Returns the filtered track,
    /// the label to show, and how many segments were dropped.</summary>
    private static async Task<(TrackTranscript Track, string Label, int Dropped)> FilterMeBleedAsync(
        TrackTranscript me, string meWav, AppConfig config, ISpeakerEmbedder embedder,
        IVoiceprintStore? voiceprints, CancellationToken ct)
    {
        var self = config.SelfSpeakerName;
        if (string.IsNullOrWhiteSpace(self) || voiceprints == null || !File.Exists(meWav))
        {
            return (me, "Me", 0);
        }

        var samples = SpeakerAudio.ReadWav16kMono(meWav);
        var kept = new List<TranscriptSegment>();
        var dropped = 0;
        foreach (var segment in me.Segments)
        {
            var embedding = embedder.Embed(SpeakerAudio.Slice(samples, segment.Start, segment.End));
            double? distance = embedding.Length == 0
                ? null
                : await voiceprints.DistanceToAsync(self, embedding, ct).ConfigureAwait(false);

            if (SpeakerIdentity.DecideMe(distance, config.SelfMatchMaxDistance, self).IsBleed)
            {
                dropped++;
            }
            else
            {
                kept.Add(segment);
            }
        }
        return (new TrackTranscript(me.Track, me.Duration, kept), self, dropped);
    }

    /// <summary>Suggest names for far-side speakers from their self-introductions: regex over
    /// the attributed transcript first, then an Ollama pass for any speaker the regex missed.
    /// Best-effort — returns whatever it found; an unreachable model just yields fewer hits.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> SuggestNamesAsync(
        TrackTranscript others, DiarizationResult result, AppConfig config, CancellationToken ct)
    {
        var lines = others.Segments
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => (Speaker: result.SpeakerFor(s), s.Text))
            .ToList();
        if (lines.Count == 0) return new Dictionary<string, string>();

        var suggestions = new Dictionary<string, string>(SpeakerNameExtractor.DetectRegex(lines));

        // LLM fallback only for far-side speakers the regex didn't name.
        var unnamed = lines.Select(l => l.Speaker)
            .Where(s => s != LiveCaptionEngine.OthersLabel && !suggestions.ContainsKey(s))
            .ToHashSet();
        if (unnamed.Count > 0)
        {
            var chat = new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive);
            if (chat.IsReachable())
            {
                var llm = await SpeakerNameExtractor
                    .ExtractWithLlmAsync(chat, config.ReasoningModel, lines, ct).ConfigureAwait(false);
                foreach (var (speaker, name) in llm)
                {
                    if (unnamed.Contains(speaker)) suggestions[speaker] = name;
                }
            }
        }
        return suggestions;
    }

    /// <summary>Discard any buffered console keystrokes so a stray Enter (e.g. pressed during
    /// the slow transcription) doesn't auto-submit the next prompt. No-op if input is
    /// redirected (piped/non-interactive).</summary>
    private static void DrainInput()
    {
        try
        {
            while (!Console.IsInputRedirected && Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        catch { /* console may not support KeyAvailable in some hosts */ }
    }
}
