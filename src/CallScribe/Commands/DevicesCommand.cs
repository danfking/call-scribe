using System.CommandLine;
using CallScribe.Audio;
using Spectre.Console;

namespace CallScribe.Commands;

public static class DevicesCommand
{
    public static Command Create()
    {
        var command = new Command("devices", "List audio devices and show which ones recording will use");
        command.SetAction(_ =>
        {
            if (LiveCaptureGuard.Unavailable()) return 1;

            var devices = CaptureBackend.Current.ListDevices();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Type");
            table.AddColumn("Device");
            table.AddColumn("Used as");

            foreach (var device in devices.Outputs)
            {
                table.AddRow(
                    "Output",
                    device.Name,
                    device.IsDefault ? "[green]Others track (loopback)[/]" : "");
            }

            foreach (var device in devices.Inputs)
            {
                table.AddRow(
                    "Input",
                    device.Name,
                    device.IsDefault ? "[green]Me track (microphone)[/]" : "");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(
                "\n[grey]Recording uses the default communications devices. " +
                "Change them in Windows sound settings; don't switch outputs mid-call.[/]");
            return 0;
        });
        return command;
    }
}
