namespace CallScribe.Audio;

/// <summary>Resolves the platform capture backend once. On Windows this is the real WASAPI/AEC
/// backend; elsewhere it is a stub that reports live capture as unsupported, so the rest of the CLI
/// (transcribe, coach, config) still runs. The compile-time switch keeps the Windows-only types out
/// of the portable build entirely, which is what lets net10.0 compile clean under the CA1416
/// platform analyzer.</summary>
internal static class CaptureBackend
{
    public static ICaptureBackend Current { get; } =
#if WINDOWS
        new WindowsCaptureBackend();
#else
        new UnsupportedCaptureBackend();
#endif

    public static bool SupportsLiveCapture => Current.SupportsLiveCapture;

    /// <summary>Shown when a live-capture command (record, start, devices, enroll-me) runs on a
    /// platform without capture support.</summary>
    public const string UnsupportedMessage =
        "Live audio capture is only available on Windows. This build can still transcribe existing " +
        "recordings ('call-scribe transcribe') and run the coach ('call-scribe coach').";
}
