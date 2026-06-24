using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>Records the default render device (loopback = everyone else) and the
/// default capture device (mic = the user) as two time-aligned WAV files.</summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly CaptureTrack _others;
    private readonly CaptureTrack _me;

    public CaptureTrack OthersTrack => _others;
    public CaptureTrack MeTrack => _me;

    public string OthersPath { get; }
    public string LoopbackDeviceName { get; }
    public string MicDeviceName { get; }

    public CaptureEngine(string stem, string recordingsDir, AppConfig? config = null, bool aecMic = false, int aecSuppressionLevel = 1)
    {
        Directory.CreateDirectory(recordingsDir);
        OthersPath = Path.Combine(recordingsDir, $"{stem}.others.wav");
        var mePath = Path.Combine(recordingsDir, $"{stem}.me.wav");

        using var enumerator = new MMDeviceEnumerator();
        var render = ResolveDevice(enumerator, DataFlow.Render, config?.LoopbackDevice);
        LoopbackDeviceName = render.FriendlyName;

        var epoch = new Stopwatch();
        _others = new CaptureTrack("Others", new WasapiLoopbackCapture(render), epoch, OthersPath);

        IWaveIn meCapture;
        if (aecMic)
        {
            // The AEC source opens the default communications mic and speaker reference
            // itself and emits 16 kHz mono. We do not resolve a mic device here, so a
            // configured micDevice is not consulted (and no MMDevice is left to leak).
            meCapture = new VoiceCaptureAecSource { EchoSuppressionLevel = aecSuppressionLevel };
            MicDeviceName = "Default communications (AEC)";
        }
        else
        {
            var mic = ResolveDevice(enumerator, DataFlow.Capture, config?.MicDevice);
            MicDeviceName = mic.FriendlyName;
            meCapture = new WasapiCapture(mic); // takes ownership of mic
        }
        _me = new CaptureTrack("Me", meCapture, epoch, mePath);

        epoch.Start();
    }

    public void Start()
    {
        _others.Start();
        _me.Start();
    }

    public async Task<TimeSpan> StopAsync()
    {
        var durations = await Task.WhenAll(_others.StopAsync(), _me.StopAsync()).ConfigureAwait(false);
        return durations.Max();
    }

    /// <summary>Per-track capture failures (device unplugged etc.). Tracks with
    /// errors still produce a finalised WAV with whatever was captured.</summary>
    public IEnumerable<(string Track, Exception Error)> Errors
    {
        get
        {
            if (_others.Error is { } o) yield return ("Others", o);
            if (_me.Error is { } m) yield return ("Me", m);
        }
    }

    /// <summary>Match a configured friendly-name substring against active devices,
    /// or fall back to the default communications endpoint.</summary>
    internal static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string? nameSubstring)
    {
        if (string.IsNullOrWhiteSpace(nameSubstring))
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }

        var match = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"No active {(flow == DataFlow.Render ? "output" : "input")} device matches '{nameSubstring}'. " +
            "Run 'call-scribe devices' to list devices, or clear the setting with 'call-scribe config set " +
            $"{(flow == DataFlow.Render ? "loopbackDevice" : "micDevice")} \"\"'.");
    }

    public void Dispose()
    {
        _others.Dispose();
        _me.Dispose();
    }
}
