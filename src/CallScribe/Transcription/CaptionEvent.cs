namespace CallScribe.Transcription;

/// <summary>A caption that was emitted to the display. Others captions fire as soon
/// as they are transcribed; Me captions fire only after surviving echo suppression
/// (suppressed bleed never fires). Consumed by tests and the echo-bleed harness.</summary>
public readonly record struct CaptionEvent(DateTime At, string Label, string Caption);
