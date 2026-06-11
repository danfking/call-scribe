using System.CommandLine;
using CallScribe;
using CallScribe.Commands;
using Spectre.Console;

try
{
    // Apply the user's output-root override (if any) before any command resolves paths.
    AppPaths.OutputRootOverride = AppConfig.Load().OutputRoot;
}
catch (InvalidDataException ex)
{
    AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
    return 1;
}

CallScribe.Transcription.WhisperNativeResolver.Apply();

var root = new RootCommand("call-scribe: local dual-track call recording and transcription");
root.Subcommands.Add(RecordCommand.Create());
root.Subcommands.Add(TranscribeCommand.Create());
root.Subcommands.Add(DevicesCommand.Create());
root.Subcommands.Add(ConfigCommand.Create());

return await root.Parse(args).InvokeAsync();
