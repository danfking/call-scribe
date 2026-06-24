using CallScribe.Transcription;
using NAudio.Wave;

namespace CallScribe.Tests;

/// <summary>Covers the pure gating helpers in the live caption engine (annotation filtering and the
/// RMS/amplitude silence math). The threaded capture/echo-frontier orchestration is exercised by the
/// LiveReplay harness, not here.</summary>
public class LiveCaptionEngineTests
{
    private static readonly WaveFormat Pcm16Mono = new(16000, 16, 1); // 32000 bytes/sec

    /// <summary>Build a 16-bit PCM mono buffer from sample values. Uses the parameterless
    /// MemoryStream + Write so GetBuffer() (which the engine calls) is permitted.</summary>
    private static MemoryStream Pcm16(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        return ms;
    }

    private static MemoryStream Constant(int count, short amplitude)
    {
        var samples = new short[count];
        Array.Fill(samples, amplitude);
        return Pcm16(samples);
    }

    [Theory]
    [InlineData("[MUSIC PLAYING]")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(wind blowing)")]
    [InlineData("*sigh*")]
    [InlineData("...")]
    [InlineData("[music] (laughs)")]
    public void IsNonSpeechAnnotation_TrueForAnnotationsOnly(string text) =>
        Assert.True(LiveCaptionEngine.IsNonSpeechAnnotation(text));

    [Theory]
    [InlineData("Hello there, shall we start?")]
    [InlineData("[music] but anyway, the report")] // annotation plus real speech
    [InlineData("72 dollars")]
    public void IsNonSpeechAnnotation_FalseWhenRealSpeechRemains(string text) =>
        Assert.False(LiveCaptionEngine.IsNonSpeechAnnotation(text));

    [Fact]
    public void PeakAmplitude_IsNearOne_ForFullScaleAndZeroForSilence()
    {
        Assert.True(LiveCaptionEngine.PeakAmplitude(Constant(16000, short.MaxValue), Pcm16Mono) > 0.99f);
        Assert.Equal(0f, LiveCaptionEngine.PeakAmplitude(Constant(16000, 0), Pcm16Mono));
    }

    [Fact]
    public void PeakRms_SeparatesSilenceFromSpeech_AtTheGate()
    {
        // SilenceRmsThreshold is 0.002f. Digital silence sits below it; a steady half-scale tone above.
        Assert.True(LiveCaptionEngine.PeakRms(Constant(16000, 0), Pcm16Mono) < 0.002f);
        Assert.True(LiveCaptionEngine.PeakRms(Constant(16000, 16384), Pcm16Mono) > 0.002f);
    }

    [Fact]
    public void IsTrailingSilence_TrueWhenTheTailIsSilent()
    {
        // 1.0s of tone followed by 0.7s of silence: the trailing 0.6s window is silent.
        var samples = new short[16000 + 11200];
        Array.Fill(samples, (short)16384, 0, 16000);
        Assert.True(LiveCaptionEngine.IsTrailingSilence(Pcm16(samples), Pcm16Mono));
    }

    [Fact]
    public void IsTrailingSilence_FalseWhenTheTailIsLoud()
    {
        Assert.False(LiveCaptionEngine.IsTrailingSilence(Constant(32000, 16384), Pcm16Mono));
    }
}
