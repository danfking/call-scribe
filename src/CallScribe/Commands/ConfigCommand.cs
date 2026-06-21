using System.CommandLine;
using Spectre.Console;

namespace CallScribe.Commands;

public static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "Show or change settings");
        command.SetAction(_ => Show());

        var keyArgument = new Argument<string>("key")
        {
            Description = "micDevice | loopbackDevice | model | language | outputRoot | keepAudio | "
                          + "coachEnabled | ollamaUrl | fastModel | reasoningModel | embedModel | "
                          + "coachRecallMaxDistance | postgresConn | speakerIdEnabled | "
                          + "diarizeAfterMeeting | voiceprintMaxDistance | speakerSegModel | speakerEmbedModel",
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

        table.AddRow("micDevice", config.MicDevice ?? "[grey](default)[/]", "default communications mic");
        table.AddRow("loopbackDevice", config.LoopbackDevice ?? "[grey](default)[/]", "default communications output");
        table.AddRow("model", config.Model, "large-v3-turbo");
        table.AddRow("language", config.Language, "en");
        table.AddRow("outputRoot", config.OutputRoot ?? "[grey](default)[/]", AppPaths.OutputRoot.EscapeMarkup());
        table.AddRow("keepAudio", config.KeepAudio.ToString().ToLowerInvariant(), "true");
        table.AddEmptyRow();
        table.AddRow("coachEnabled", config.CoachEnabled.ToString().ToLowerInvariant(), "false");
        table.AddRow("ollamaUrl", config.OllamaUrl.EscapeMarkup(), "http://localhost:11434");
        table.AddRow("fastModel", config.FastModel.EscapeMarkup(), "qwen3:4b");
        table.AddRow("reasoningModel", config.ReasoningModel.EscapeMarkup(), "llama3.1:8b");
        table.AddRow("embedModel", config.EmbedModel.EscapeMarkup(), "nomic-embed-text");
        table.AddRow("coachRecallMaxDistance", config.CoachRecallMaxDistance.ToString("0.##"), "0.35");
        table.AddRow("postgresConn", "[grey](hidden)[/]", "localhost:5432/callscribe");
        table.AddEmptyRow();
        table.AddRow("speakerIdEnabled", config.SpeakerIdEnabled.ToString().ToLowerInvariant(), "false");
        table.AddRow("diarizeAfterMeeting", config.DiarizeAfterMeeting.ToString().ToLowerInvariant(), "true");
        table.AddRow("voiceprintMaxDistance", config.VoiceprintMaxDistance.ToString("0.##"), "0.3");
        table.AddRow("speakerSegModel", config.SpeakerSegModel.EscapeMarkup(), "sherpa-onnx-pyannote-segmentation-3-0.onnx");
        table.AddRow("speakerEmbedModel", config.SpeakerEmbedModel.EscapeMarkup(), "nemo_en_titanet_small.onnx");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Config file: {AppConfig.ConfigPath.EscapeMarkup()}[/]");
        return 0;
    }

    private static int Set(string key, string value)
    {
        var config = AppConfig.Load();
        var cleared = string.IsNullOrWhiteSpace(value);

        switch (key.ToLowerInvariant())
        {
            case "micdevice":
                config.MicDevice = cleared ? null : value;
                break;
            case "loopbackdevice":
                config.LoopbackDevice = cleared ? null : value;
                break;
            case "model":
                if (!cleared) Transcription.ModelManager.ParseModel(value); // validate
                config.Model = cleared ? "large-v3-turbo" : value;
                break;
            case "language":
                config.Language = cleared ? "en" : value;
                break;
            case "outputroot":
                if (!cleared && LooksSynced(value))
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Warning:[/] that path looks like a synced folder (OneDrive/Dropbox/Documents). " +
                        "Call recordings will sync to that service.");
                }
                config.OutputRoot = cleared ? null : Path.GetFullPath(value);
                break;
            case "keepaudio":
                config.KeepAudio = cleared || bool.Parse(value);
                break;
            case "coachenabled":
                config.CoachEnabled = !cleared && bool.Parse(value);
                break;
            case "ollamaurl":
                config.OllamaUrl = cleared ? "http://localhost:11434" : value;
                break;
            case "fastmodel":
                config.FastModel = cleared ? "qwen3:4b" : value;
                break;
            case "reasoningmodel":
                config.ReasoningModel = cleared ? "llama3.1:8b" : value;
                break;
            case "embedmodel":
                config.EmbedModel = cleared ? "nomic-embed-text" : value;
                break;
            case "coachrecallmaxdistance":
                config.CoachRecallMaxDistance = cleared ? 0.35 : double.Parse(value);
                break;
            case "postgresconn":
                config.PostgresConn = cleared
                    ? "Host=localhost;Port=5432;Database=callscribe;Username=postgres;Password=postgres"
                    : value;
                break;
            case "speakeridenabled":
                config.SpeakerIdEnabled = !cleared && bool.Parse(value);
                break;
            case "diarizeaftermeeting":
                config.DiarizeAfterMeeting = cleared || bool.Parse(value);
                break;
            case "voiceprintmaxdistance":
                config.VoiceprintMaxDistance = cleared ? 0.30 : double.Parse(value);
                break;
            case "speakersegmodel":
                config.SpeakerSegModel = cleared ? "sherpa-onnx-pyannote-segmentation-3-0.onnx" : value;
                break;
            case "speakerembedmodel":
                config.SpeakerEmbedModel = cleared ? "nemo_en_titanet_small.onnx" : value;
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown setting '{key.EscapeMarkup()}'.[/]");
                return 1;
        }

        config.Save();
        AnsiConsole.MarkupLine($"[green]Saved.[/] {key} = {(cleared ? "(default)" : value.EscapeMarkup())}");
        return 0;
    }

    private static bool LooksSynced(string path) =>
        path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("Documents", StringComparison.OrdinalIgnoreCase);
}
