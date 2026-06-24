using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>Capture backend for platforms without portable loopback/mic capture (everything but
/// Windows). Live commands guard on <see cref="ICaptureBackend.SupportsLiveCapture"/> before calling
/// these, so reaching a method here is a programming error rather than a user-facing one. Compiled on
/// both target frameworks: it has no Windows dependency.</summary>
internal sealed class UnsupportedCaptureBackend : ICaptureBackend
{
    public bool SupportsLiveCapture => false;

    public SessionCaptures OpenSession(AppConfig? config, bool aecMic, int aecSuppressionLevel) => throw Unsupported();

    public IWaveIn OpenMic(AppConfig config) => throw Unsupported();

    public DeviceListing ListDevices() => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new(CaptureBackend.UnsupportedMessage);
}
