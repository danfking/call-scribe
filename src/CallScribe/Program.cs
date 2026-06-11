using System.CommandLine;
using CallScribe.Commands;

var root = new RootCommand("call-scribe: local dual-track call recording and transcription");
root.Subcommands.Add(RecordCommand.Create());
root.Subcommands.Add(DevicesCommand.Create());

return await root.Parse(args).InvokeAsync();
