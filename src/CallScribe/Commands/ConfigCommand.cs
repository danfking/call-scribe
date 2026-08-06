using System.CommandLine;
using Spectre.Console;

namespace CallScribe.Commands;

public static class ConfigCommand
{
    /// <summary>One settable config field: how to show it, and how to apply a new value (null = reset
    /// to default). The registry below is the single source of truth, so <c>set</c>, the table in
    /// <c>show</c>, and the <c>key</c> argument's help can never drift apart.</summary>
    private sealed record ConfigSetting(
        string Key,
        Func<string> Default,
        Func<AppConfig, string> Value,
        Action<AppConfig, string?> Apply,
        bool StartsGroup = false);

    private static readonly IReadOnlyList<ConfigSetting> Settings =
    [
        new("micDevice", () => "default communications mic",
            c => c.MicDevice ?? "[grey](default)[/]",
            (c, v) => c.MicDevice = v),
        new("loopbackDevice", () => "default communications output",
            c => c.LoopbackDevice ?? "[grey](default)[/]",
            (c, v) => c.LoopbackDevice = v),
        new("model", () => "large-v3-turbo",
            c => c.Model.EscapeMarkup(),
            (c, v) => { if (v != null) Transcription.ModelManager.ParseModel(v); c.Model = v ?? "large-v3-turbo"; }),
        new("liveModel", () => "small.en",
            c => c.LiveModel.EscapeMarkup(),
            (c, v) => { if (v != null) Transcription.ModelManager.ParseModel(v); c.LiveModel = v ?? "small.en"; }),
        new("liveMeSpeechThreshold", () => "0.01",
            c => c.LiveMeSpeechThreshold.ToString("0.###"),
            (c, v) => c.LiveMeSpeechThreshold = v == null ? 0.01 : double.Parse(v)),
        new("language", () => "en",
            c => c.Language.EscapeMarkup(),
            (c, v) => c.Language = v ?? "en"),
        new("outputRoot", () => AppPaths.OutputRoot.EscapeMarkup(),
            c => c.OutputRoot ?? "[grey](default)[/]",
            (c, v) =>
            {
                if (v != null && LooksSynced(v))
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Warning:[/] that path looks like a synced folder (OneDrive/Dropbox/Documents). " +
                        "Call recordings will sync to that service.");
                }
                c.OutputRoot = v == null ? null : Path.GetFullPath(v);
            }),
        new("keepAudio", () => "true",
            c => c.KeepAudio.ToString().ToLowerInvariant(),
            (c, v) => c.KeepAudio = v == null || bool.Parse(v)),

        new("coachEnabled", () => "false",
            c => c.CoachEnabled.ToString().ToLowerInvariant(),
            (c, v) => c.CoachEnabled = v != null && bool.Parse(v), StartsGroup: true),
        new("ollamaUrl", () => "http://localhost:11434",
            c => c.OllamaUrl.EscapeMarkup(),
            (c, v) => c.OllamaUrl = v ?? "http://localhost:11434"),
        new("fastModel", () => "qwen3:4b-instruct",
            c => c.FastModel.EscapeMarkup(),
            (c, v) => c.FastModel = v ?? "qwen3:4b-instruct"),
        new("reasoningModel", () => "llama3.1:8b",
            c => c.ReasoningModel.EscapeMarkup(),
            (c, v) => c.ReasoningModel = v ?? "llama3.1:8b"),
        new("embedModel", () => "nomic-embed-text",
            c => c.EmbedModel.EscapeMarkup(),
            (c, v) => c.EmbedModel = v ?? "nomic-embed-text"),
        new("ollamaKeepAlive", () => "10m",
            c => c.OllamaKeepAlive.EscapeMarkup(),
            (c, v) => c.OllamaKeepAlive = v ?? "10m"),
        new("coachRecallMaxDistance", () => "0.35",
            c => c.CoachRecallMaxDistance.ToString("0.##"),
            (c, v) => c.CoachRecallMaxDistance = v == null ? 0.35 : double.Parse(v)),
        new("postgresConn", () => "localhost:5432/callscribe",
            _ => "[grey](hidden)[/]",
            (c, v) => c.PostgresConn = v
                ?? "Host=localhost;Port=5432;Database=callscribe;Username=postgres;Password=postgres"),

        new("coachingProfilesEnabled", () => "true",
            c => c.CoachingProfilesEnabled.ToString().ToLowerInvariant(),
            (c, v) => c.CoachingProfilesEnabled = v == null || bool.Parse(v), StartsGroup: true),
        new("coachingProfilesDir", () => AppPaths.CoachingDir.EscapeMarkup(),
            c => c.CoachingProfilesDir?.EscapeMarkup() ?? "[grey](default)[/]",
            (c, v) =>
            {
                if (v != null && LooksSynced(v))
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Warning:[/] that path looks like a synced folder (OneDrive/Dropbox/Documents). " +
                        "Private coaching notes will sync to that service.");
                }
                c.CoachingProfilesDir = v == null ? null : Path.GetFullPath(v);
            }),

        new("speakerIdEnabled", () => "false",
            c => c.SpeakerIdEnabled.ToString().ToLowerInvariant(),
            (c, v) => c.SpeakerIdEnabled = v != null && bool.Parse(v), StartsGroup: true),
        new("diarizeAfterMeeting", () => "true",
            c => c.DiarizeAfterMeeting.ToString().ToLowerInvariant(),
            (c, v) => c.DiarizeAfterMeeting = v == null || bool.Parse(v)),
        new("voiceprintMaxDistance", () => "0.3",
            c => c.VoiceprintMaxDistance.ToString("0.##"),
            (c, v) => c.VoiceprintMaxDistance = v == null ? 0.30 : double.Parse(v)),
        new("speakerSegModel", () => "sherpa-onnx-pyannote-segmentation-3-0.onnx",
            c => c.SpeakerSegModel.EscapeMarkup(),
            (c, v) => c.SpeakerSegModel = v ?? "sherpa-onnx-pyannote-segmentation-3-0.onnx"),
        new("speakerEmbedModel", () => "nemo_en_titanet_small.onnx",
            c => c.SpeakerEmbedModel.EscapeMarkup(),
            (c, v) => c.SpeakerEmbedModel = v ?? "nemo_en_titanet_small.onnx"),
        new("selfSpeakerName", () => "(none)",
            c => c.SelfSpeakerName ?? "[grey](not enrolled)[/]",
            (c, v) => c.SelfSpeakerName = v),
        new("selfMatchMaxDistance", () => "0.6",
            c => c.SelfMatchMaxDistance.ToString("0.##"),
            (c, v) => c.SelfMatchMaxDistance = v == null ? 0.6 : double.Parse(v)),
        new("diarizationClusterThreshold", () => "0.75",
            c => c.DiarizationClusterThreshold.ToString("0.##"),
            (c, v) => c.DiarizationClusterThreshold = v == null ? 0.75f : float.Parse(v)),
        new("diarizationMinClusterSeconds", () => "8",
            c => c.DiarizationMinClusterSeconds.ToString("0.##"),
            (c, v) => c.DiarizationMinClusterSeconds = v == null ? 8.0 : double.Parse(v)),
        new("sessionMergeDistance", () => "0.7",
            c => c.SessionMergeDistance.ToString("0.##"),
            (c, v) => c.SessionMergeDistance = v == null ? 0.70 : double.Parse(v)),
        new("liveMinSpeakerSeconds", () => "1.5",
            c => c.LiveMinSpeakerSeconds.ToString("0.##"),
            (c, v) => c.LiveMinSpeakerSeconds = v == null ? 1.5 : double.Parse(v)),
        new("speakerConsolidationDistance", () => "0.8",
            c => c.SpeakerConsolidationDistance.ToString("0.##"),
            (c, v) => c.SpeakerConsolidationDistance = v == null ? 0.80 : double.Parse(v)),
        new("speakerConsolidationMinClips", () => "3",
            c => c.SpeakerConsolidationMinClips.ToString(),
            (c, v) => c.SpeakerConsolidationMinClips = v == null ? 3 : int.Parse(v)),
    ];

    public static Command Create()
    {
        var command = new Command("config", "Show or change settings");
        command.SetAction(_ => Show());

        var keyArgument = new Argument<string>("key")
        {
            Description = string.Join(" | ", Settings.Select(s => s.Key)),
        };
        var valueArgument = new Argument<string>("value")
        {
            Description = "New value; use \"\" to reset to the default",
        };
        var set = new Command("set", "Change a setting");
        set.Arguments.Add(keyArgument);
        set.Arguments.Add(valueArgument);
        set.SetAction(parseResult => Set(
            parseResult.GetValue(keyArgument)!,
            parseResult.GetValue(valueArgument)!));
        command.Subcommands.Add(set);

        return command;
    }

    private static int Show()
    {
        var config = AppConfig.Load();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddColumn("Default");

        foreach (var setting in Settings)
        {
            if (setting.StartsGroup) table.AddEmptyRow();
            table.AddRow(setting.Key, setting.Value(config), setting.Default());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Config file: {AppConfig.ConfigPath.EscapeMarkup()}[/]");
        return 0;
    }

    private static int Set(string key, string value)
    {
        var setting = Settings.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (setting == null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown setting '{key.EscapeMarkup()}'.[/]");
            return 1;
        }

        var config = AppConfig.Load();
        var cleared = string.IsNullOrWhiteSpace(value);
        setting.Apply(config, cleared ? null : value);
        config.Save();

        AnsiConsole.MarkupLine($"[green]Saved.[/] {setting.Key} = {(cleared ? "(default)" : value.EscapeMarkup())}");
        return 0;
    }

    /// <summary>The settable keys and their current display values, for the interactive shell's config
    /// editor. Read-only: writes go back through `config set` so the validation in <see cref="Set"/>
    /// stays the single path. Display values may contain Spectre markup (e.g. "[grey](default)[/]").</summary>
    public static IReadOnlyList<(string Key, string Display)> ListSettings()
    {
        var config = AppConfig.Load();
        return [.. Settings.Select(s => (s.Key, s.Value(config)))];
    }

    private static bool LooksSynced(string path) =>
        path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Documents", StringComparison.OrdinalIgnoreCase);
}
