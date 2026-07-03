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
root.Subcommands.Add(CoachCommand.Create());

// No command given: open the interactive home screen (menu + typed palette). Under a pipe or a
// TTY-less host (Docker without -it) input is redirected, so fall through to the usual help output
// instead of blocking on a prompt. Explicit `call-scribe <command>` always takes the direct path.
if (args.Length == 0 && !Console.IsInputRedirected)
    return await CallScribe.Commands.InteractiveShell.RunAsync(root, CancellationToken.None);

return await root.Parse(args).InvokeAsync();
