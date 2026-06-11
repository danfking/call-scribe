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
    public string MePath { get; }
    public string LoopbackDeviceName { get; }
    public string MicDeviceName { get; }

    public CaptureEngine(string stem, string recordingsDir, AppConfig? config = null)
    {
        Directory.CreateDirectory(recordingsDir);
        OthersPath = Path.Combine(recordingsDir, $"{stem}.others.wav");
        MePath = Path.Combine(recordingsDir, $"{stem}.me.wav");

        using var enumerator = new MMDeviceEnumerator();
        var render = ResolveDevice(enumerator, DataFlow.Render, config?.LoopbackDevice);
        var mic = ResolveDevice(enumerator, DataFlow.Capture, config?.MicDevice);
        LoopbackDeviceName = render.FriendlyName;
        MicDeviceName = mic.FriendlyName;

        var epoch = new Stopwatch();
        _others = new CaptureTrack("Others", new WasapiLoopbackCapture(render), epoch, OthersPath);
        _me = new CaptureTrack("Me", new WasapiCapture(mic), epoch, MePath);

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
    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string? nameSubstring)
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
