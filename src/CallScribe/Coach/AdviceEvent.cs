namespace CallScribe.Coach;

public enum AdviceKind { Tip, Answer, Warning }

/// <summary>A piece of coaching advice surfaced to the panel. Mirrors the shape of
/// <see cref="CallScribe.Transcription.CaptionEvent"/> so the UI can render both the
/// same way. <see cref="Glyph"/> and <see cref="Colour"/> are presentation hints the
/// display can use without taking a dependency on this namespace.</summary>
public readonly record struct AdviceEvent(DateTime At, AdviceKind Kind, string Text, string Source)
{
    public string Glyph => Kind switch
    {
        AdviceKind.Answer => "?",
        AdviceKind.Warning => "!",
        _ => "*",
    };

    public string Colour => Kind switch
    {
        AdviceKind.Answer => "green",
        AdviceKind.Warning => "red",
        _ => "magenta",
    };
}
