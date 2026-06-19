using System.CommandLine;
using CallScribe;
using CallScribe.Commands;
using Spectre.Console;

// Use UTF-8 output so geometric icons and box-drawing glyphs render instead of "?"
// under the legacy console code page. Guarded for redirected / console-less hosts.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console */ }

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
root.Subcommands.Add(ListenCommand.Create());
root.Subcommands.Add(TranscribeCommand.Create());
root.Subcommands.Add(DevicesCommand.Create());
root.Subcommands.Add(ConfigCommand.Create());

return await root.Parse(args).InvokeAsync();
