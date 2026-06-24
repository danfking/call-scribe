using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>The real capture backend: WASAPI loopback for the Others track, WASAPI (or the AEC DMO)
/// for the Me track, and MMDevice enumeration for device listing. Compiled only into the
/// net10.0-windows build; the portable build uses <see cref="UnsupportedCaptureBackend"/> instead.</summary>
internal sealed class WindowsCaptureBackend : ICaptureBackend
{
    public bool SupportsLiveCapture => true;

    public SessionCaptures OpenSession(AppConfig? config, bool aecMic, int aecSuppressionLevel)
    {
        using var enumerator = new MMDeviceEnumerator();
        var render = ResolveDevice(enumerator, DataFlow.Render, config?.LoopbackDevice);
        IWaveIn others = new WasapiLoopbackCapture(render);
        try
        {
            IWaveIn me;
            string meName;
            if (aecMic)
            {
                // The AEC source opens the default communications mic and speaker reference itself
                // and emits 16 kHz mono. No mic device is resolved here, so a configured micDevice is
                // not consulted (and no MMDevice is left to leak).
                me = new VoiceCaptureAecSource { EchoSuppressionLevel = aecSuppressionLevel };
                meName = "Default communications (AEC)";
            }
            else
            {
                var mic = ResolveDevice(enumerator, DataFlow.Capture, config?.MicDevice);
                meName = mic.FriendlyName;
                me = new WasapiCapture(mic); // takes ownership of mic
            }

            return new SessionCaptures(others, render.FriendlyName, me, meName);
        }
        catch
        {
            // The Me-side open failed (a configured micDevice matched nothing, or the AEC source
            // could not init): dispose the loopback capture already created so it does not leak the
            // render endpoint.
            others.Dispose();
            throw;
        }
    }

    public IWaveIn OpenMic(AppConfig config)
    {
        using var enumerator = new MMDeviceEnumerator();
        var mic = ResolveDevice(enumerator, DataFlow.Capture, config.MicDevice);
        return new WasapiCapture(mic); // takes ownership of mic
    }

    public DeviceListing ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return new DeviceListing(
            Endpoints(enumerator, DataFlow.Render, DefaultEndpointId(enumerator, DataFlow.Render)),
            Endpoints(enumerator, DataFlow.Capture, DefaultEndpointId(enumerator, DataFlow.Capture)));
    }

    private static List<AudioEndpointInfo> Endpoints(MMDeviceEnumerator enumerator, DataFlow flow, string? defaultId)
    {
        var endpoints = new List<AudioEndpointInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            // EnumerateAudioEndPoints hands back IDisposable COM wrappers; dispose each one rather
            // than leaving native endpoint handles to a finalizer.
            using (device)
            {
                endpoints.Add(new AudioEndpointInfo(device.FriendlyName, device.ID == defaultId));
            }
        }
        return endpoints;
    }

    /// <summary>The default communications endpoint's ID, or null when no device holds that role
    /// (so the listing still shows the active devices instead of throwing).</summary>
    private static string? DefaultEndpointId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Match a configured friendly-name substring against active devices, or fall back to
    /// the default communications endpoint.</summary>
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
}
