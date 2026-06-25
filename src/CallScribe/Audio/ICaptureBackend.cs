using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>Platform seam for live audio capture. The Windows backend uses WASAPI loopback for the
/// Others track and WASAPI (or the AEC DMO) for the Me track; other platforms get an unsupported
/// backend so the portable net10.0 build compiles without the Windows-only audio APIs. Everything
/// downstream of a WAV (transcribe, diarize, coach) is platform-neutral and does not go through here.</summary>
internal interface ICaptureBackend
{
    /// <summary>False on platforms with no portable loopback/mic capture (everything but Windows).
    /// Live commands check this and degrade with a clear message rather than throwing.</summary>
    bool SupportsLiveCapture { get; }

    /// <summary>Open the two session captures: Others (system loopback) and Me (microphone, optionally
    /// echo-cancelled). The returned sources are owned by the caller and disposed via CaptureTrack.</summary>
    SessionCaptures OpenSession(AppConfig? config, bool aecMic, int aecSuppressionLevel);

    /// <summary>Open the configured microphone for a one-off enrollment clip. Caller owns the source.</summary>
    IWaveIn OpenMic(AppConfig config);

    /// <summary>List the active output and input endpoints for the devices command.</summary>
    DeviceListing ListDevices();
}

/// <summary>The two raw capture sources for a recording session, plus their friendly device names.</summary>
internal sealed record SessionCaptures(IWaveIn Others, string OthersName, IWaveIn Me, string MeName);

/// <summary>Active audio endpoints split by direction, for the devices listing.</summary>
internal sealed record DeviceListing(
    IReadOnlyList<AudioEndpointInfo> Outputs,
    IReadOnlyList<AudioEndpointInfo> Inputs);

/// <summary>One audio endpoint: its friendly name and whether it is the default communications device.</summary>
internal sealed record AudioEndpointInfo(string Name, bool IsDefault);
