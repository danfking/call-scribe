using System.CommandLine;
using CallScribe.Coach.Mock;
using CallScribe.Rpg;
using CallScribe.Transcription;
using Spectre.Console;

namespace CallScribe.Commands;

public static class RpgCommand
{
    public static Command Create()
    {
        var scriptArgument = new Argument<string>("script")
        {
            Description = "Path to a JSONL meeting script ({\"t\": seconds, \"speaker\": \"Me|Others|Name\", \"text\": \"...\"})",
        };
        var fastOption = new Option<bool>("--fast")
        {
            Description = "Ignore timestamps and replay as fast as possible (time-window rules like "
                          + "silence and combos need real pacing, so prefer the default for a feel check)",
        };

        var replay = new Command("replay", "Replay a scripted meeting through the RPG boss fight (no audio) for testing");
        replay.Arguments.Add(scriptArgument);
        replay.Options.Add(fastOption);
        replay.SetAction((parseResult, ct) =>
            ReplayAsync(parseResult.GetValue(scriptArgument)!, parseResult.GetValue(fastOption), ct));

        var command = new Command("rpg", "Play the meeting as a co-op RPG boss fight (experimental)");
        command.Subcommands.Add(replay);
        return command;
    }

    private static async Task<int> ReplayAsync(string script, bool fast, CancellationToken ct)
    {
        if (!File.Exists(script))
        {
            AnsiConsole.MarkupLine($"[red]Script not found:[/] {script.EscapeMarkup()}");
            return 1;
        }

        var config = AppConfig.Load();
        using var display = new LiveStatusDisplay();
        display.Configure("rpg replay");
        display.Register(LiveCaptionEngine.OthersLabel, "yellow");
        display.Register(LiveCaptionEngine.MeLabel, "cyan");

        var rpg = new RpgModule(config.SelfSpeakerName);
        display.RegisterModule(rpg);
        display.SetActiveModule(rpg.Id); // active so it observes captions and owns the slot

        await MockMeetingDriver.ReplayAsync(script, display, rpg.Observe, realtime: !fast, ct).ConfigureAwait(false);
        await display.CompleteModulesAsync().ConfigureAwait(false);
        display.DisposeModules();
        display.Shutdown();
        return 0;
    }
}
