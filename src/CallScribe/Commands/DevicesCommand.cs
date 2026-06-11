using System.CommandLine;
using NAudio.CoreAudioApi;
using Spectre.Console;

namespace CallScribe.Commands;

public static class DevicesCommand
{
    public static Command Create()
    {
        var command = new Command("devices", "List audio devices and show which ones recording will use");
        command.SetAction(_ =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            var defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Type");
            table.AddColumn("Device");
            table.AddColumn("Used as");

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var isDefault = device.ID == defaultRender.ID;
                table.AddRow(
                    "Output",
                    device.FriendlyName,
                    isDefault ? "[green]Others track (loopback)[/]" : "");
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var isDefault = device.ID == defaultCapture.ID;
                table.AddRow(
                    "Input",
                    device.FriendlyName,
                    isDefault ? "[green]Me track (microphone)[/]" : "");
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
