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

    public string OthersPath { get; }
    public string MePath { get; }
    public string LoopbackDeviceName { get; }
    public string MicDeviceName { get; }

    public CaptureEngine(string stem, string recordingsDir)
    {
        Directory.CreateDirectory(recordingsDir);
        OthersPath = Path.Combine(recordingsDir, $"{stem}.others.wav");
        MePath = Path.Combine(recordingsDir, $"{stem}.me.wav");

        using var enumerator = new MMDeviceEnumerator();
        var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
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

    public void Dispose()
    {
        _others.Dispose();
        _me.Dispose();
    }
}
