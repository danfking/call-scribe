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
    public static async Task RunAsync(string stemPath, AppConfig config, bool interactive, CancellationToken ct)
    {
        var othersWav = $"{stemPath}.others.wav";
        var othersJson = $"{stemPath}.others.json";
        var meJson = $"{stemPath}.me.json";
        if (!File.Exists(othersWav) || !File.Exists(othersJson) || !File.Exists(meJson))
        {
            AnsiConsole.MarkupLine(
                "[grey]Speaker attribution skipped: recording or transcript missing (needs keepAudio).[/]");
            return;
        }

        var embedder = SpeakerIdentity.TryCreateEmbedder(config);
        using var diarizer = SpeakerIdentity.TryCreateDiarizer(config);
        if (embedder == null || diarizer == null)
        {
            embedder?.Dispose();
            AnsiConsole.MarkupLine(
                "[grey]Speaker attribution skipped: speaker models not installed "
                + "(run scripts/coach-pull-speaker-models.ps1).[/]");
            return;
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
                return;
            }

            if (interactive && voiceprints != null)
            {
                foreach (var cluster in result.Clusters.Where(c => !c.Enrolled).ToList())
                {
                    var name = AnsiConsole.Prompt(
                        new TextPrompt<string>($"Name for [yellow]{cluster.Name}[/] (Enter to skip):")
                            .AllowEmpty());
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    await voiceprints.EnrollAsync(name.Trim(), cluster.MeanEmbedding, ct).ConfigureAwait(false);
                    result.Rename(cluster.Index, name.Trim());
                    AnsiConsole.MarkupLine($"[green]Enrolled[/] {name.Trim().EscapeMarkup()} for next time.");
                }
            }

            // Rewrite the merged transcript with the resolved names.
            var others = TrackTranscript.Load(othersJson);
            var me = TrackTranscript.Load(meJson);
            var stem = Path.GetFileName(stemPath);
            var path = TranscriptMerger.Merge(stem, others, me, AppPaths.TranscriptsDir, result.SpeakerFor);

            var names = string.Join(", ", result.Clusters.Select(c => c.Name));
            AnsiConsole.MarkupLine($"[green]Speaker-attributed transcript[/] -> {path.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[grey]Far-side speakers: {names.EscapeMarkup()}[/]");
        }
        finally
        {
            embedder.Dispose();
            if (voiceprints != null) await voiceprints.DisposeAsync().ConfigureAwait(false);
        }
    }
}
