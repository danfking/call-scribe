using CallScribe.Audio;
using Spectre.Console;

namespace CallScribe.Commands;

/// <summary>Shared front-door check for the commands that need live audio capture (record, listen,
/// devices, coach enroll-me). One copy of the message and the test, so the off-Windows experience
/// stays consistent across commands.</summary>
internal static class LiveCaptureGuard
{
    /// <summary>True (and prints the reason) when this build cannot capture live audio, so a command
    /// can bail with a clear message instead of throwing from the capture backend.</summary>
    public static bool Unavailable()
    {
        if (CaptureBackend.SupportsLiveCapture) return false;
        AnsiConsole.MarkupLine($"[yellow]{CaptureBackend.UnsupportedMessage.EscapeMarkup()}[/]");
        return true;
    }
}
