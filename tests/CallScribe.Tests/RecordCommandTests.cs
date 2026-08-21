using CallScribe.Commands;
using NAudio.Wave;

namespace CallScribe.Tests;

public class RecordCommandTests
{
    private static string NewWavPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "call-scribe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "track.wav");
    }

    [Fact]
    public void WavDuration_ReadsAFinalisedHeader()
    {
        var path = NewWavPath();
        var format = new WaveFormat(16000, 16, 1);
        using (var writer = new WaveFileWriter(path, format))
        {
            writer.Write(new byte[format.AverageBytesPerSecond * 2], 0, format.AverageBytesPerSecond * 2);
        }

        Assert.Equal(TimeSpan.FromSeconds(2), RecordCommand.WavDuration(path));
    }

    [Fact]
    public void WavDuration_ReturnsNullForAnUnfinalisedHeader()
    {
        // A killed worker never rewrites the RIFF/data sizes, so the header still says zero
        // audio while the file holds plenty. TotalTime is then 00:00, not an exception; that
        // must come back as null so MergeLive falls back to the caption span instead of
        // stamping `duration: 00:00` into the frontmatter.
        var path = NewWavPath();
        using (var stream = new FileStream(path, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8);
            writer.Write(36); // placeholder chunk size, as an unflushed header leaves it
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);      // PCM
            writer.Write((short)1);      // mono
            writer.Write(16000);         // sample rate
            writer.Write(32000);         // byte rate
            writer.Write((short)2);      // block align
            writer.Write((short)16);     // bits
            writer.Write("data"u8);
            writer.Write(0);             // placeholder data size: the kill happened before finalise
            writer.Write(new byte[64000]); // the audio that was already streamed to disk
        }

        Assert.Null(RecordCommand.WavDuration(path));
    }

    [Fact]
    public void WavDuration_ReturnsNullForAMissingOrCorruptFile()
    {
        Assert.Null(RecordCommand.WavDuration(Path.Combine(Path.GetTempPath(), "no-such.wav")));
    }
}
