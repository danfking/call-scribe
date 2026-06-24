using System.Diagnostics;

namespace CallScribe.Audio;

/// <summary>Records the default render device (loopback = everyone else) and the default capture
/// device (mic = the user) as two time-aligned WAV files. Device and capture creation go through the
/// platform <see cref="CaptureBackend"/>; the time-alignment and WAV writing here are
/// platform-neutral.</summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly CaptureTrack _others;
    private readonly CaptureTrack _me;

    public CaptureTrack OthersTrack => _others;
    public CaptureTrack MeTrack => _me;

    public string OthersPath { get; }
    public string LoopbackDeviceName { get; }
    public string MicDeviceName { get; }

    public CaptureEngine(string stem, string recordingsDir, AppConfig? config = null, bool aecMic = false, int aecSuppressionLevel = 0)
    {
        Directory.CreateDirectory(recordingsDir);
        OthersPath = Path.Combine(recordingsDir, $"{stem}.others.wav");
        var mePath = Path.Combine(recordingsDir, $"{stem}.me.wav");

        var captures = CaptureBackend.Current.OpenSession(config, aecMic, aecSuppressionLevel);
        LoopbackDeviceName = captures.OthersName;
        MicDeviceName = captures.MeName;

        var epoch = new Stopwatch();
        _others = new CaptureTrack("Others", captures.Others, epoch, OthersPath);
        _me = new CaptureTrack("Me", captures.Me, epoch, mePath);

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

    /// <summary>Per-track capture failures (device unplugged etc.). Tracks with errors still produce
    /// a finalised WAV with whatever was captured.</summary>
    public IEnumerable<(string Track, Exception Error)> Errors
    {
        get
        {
            if (_others.Error is { } o) yield return ("Others", o);
            if (_me.Error is { } m) yield return ("Me", m);
        }
    }

    public void Dispose()
    {
        _others.Dispose();
        _me.Dispose();
    }
}
