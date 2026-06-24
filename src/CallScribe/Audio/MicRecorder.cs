using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>Records a short fixed-length clip from the configured microphone to a temp WAV, for
/// one-off voice enrollment (<c>coach enroll-me</c>). The device open goes through the platform
/// <see cref="CaptureBackend"/> so it captures the same mic a real session would; the WAV is in the
/// device's native format and callers resample it (e.g. SpeakerAudio.ReadWav16kMono).</summary>
public static class MicRecorder
{
    public static async Task<string> RecordToTempWavAsync(AppConfig config, TimeSpan duration, CancellationToken ct)
    {
        using var capture = CaptureBackend.Current.OpenMic(config);

        var path = Path.Combine(Path.GetTempPath(), $"call-scribe-enroll-{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(path, capture.WaveFormat);
        var stopped = new TaskCompletionSource();

        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, _) => stopped.TrySetResult();

        capture.StartRecording();
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            capture.StopRecording();
            await stopped.Task.ConfigureAwait(false); // ensure no DataAvailable races the writer dispose
        }
        return path;
    }
}
