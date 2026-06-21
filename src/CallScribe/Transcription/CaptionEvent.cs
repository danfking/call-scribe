namespace CallScribe.Transcription;

/// <summary>A caption that was emitted to the display. Others captions fire as soon
/// as they are transcribed; Me captions fire only after surviving echo suppression
/// (suppressed bleed never fires). Consumed by tests and the echo-bleed harness.
///
/// <para><see cref="Label"/> is the audio <em>channel</em> ("Me" or "Others") and is what
/// the echo filter and dashboard colours key off. <see cref="Speaker"/> is the resolved
/// person name once speaker identification has attributed the utterance (else null); use
/// <see cref="SpeakerName"/> to read "the best name we have" — the person if known,
/// otherwise the channel label.</para></summary>
public readonly record struct CaptionEvent(DateTime At, string Label, string Caption, string? Speaker = null)
{
    /// <summary>Resolved person name if known, else the channel label. This is what advice
    /// and memory attribute the utterance to.</summary>
    public string SpeakerName => Speaker ?? Label;
}
