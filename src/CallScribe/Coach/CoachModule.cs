using CallScribe.Transcription;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CallScribe.Coach;

/// <summary>The coach dashboard component: wraps a <see cref="CoachEngine"/> and owns the advice
/// panel's rendering (the recent-advice block and the activity icon that used to live in
/// LiveStatusDisplay). Observation is unconditional so the engine keeps persisting the live
/// transcript, but the model work is gated on <see cref="SetActive"/>, so a hidden coach makes no
/// Ollama calls. State is updated on the engine's loop thread and read on the render thread, so the
/// panel fields are guarded by <see cref="_lock"/>.</summary>
public sealed class CoachModule : ILiveModule
{
    // A small fixed block of recent advice stacks below the transcript; the transcript reserves
    // that much vertical space (advice rows + the panel's own borders).
    private const int CoachAdviceRows = 6;
    private const int CoachPanelRows = CoachAdviceRows + 2;
    private const int MaxAdvice = 500;

    private readonly CoachEngine _engine;
    private readonly Lock _lock = new();
    private readonly List<Advice> _advice = [];
    private (string Text, string Colour) _activity;

    private readonly record struct Advice(DateTime At, string Colour, string Glyph, string Text);

    public CoachModule(CoachEngine engine)
    {
        _engine = engine;
        // Hidden until shown: persistence still runs, advice does not (see CoachEngine.SetAdviceActive).
        _engine.SetAdviceActive(false);
        _engine.AdviceEmitted += OnAdvice;
        _engine.ActivityChanged += OnActivity;
        _activity = Present(CoachActivity.Listening);
    }

    public string Id => "coach";
    public string Title => "Coach";

    public event Action? Changed;
    public event Action<string>? Narrated;

    /// <summary>Feed a caption to the engine. Unconditional (unlike the RPG module): the engine
    /// persists the live transcript, which must stay complete whether or not the panel is shown.</summary>
    public void Observe(CaptionEvent caption) => _engine.Observe(caption);

    public void SetActive(bool active) => _engine.SetAdviceActive(active);

    public int ReserveRows(int maxRows) => CoachPanelRows;

    public Task CompleteAsync() => _engine.CompleteAsync();

    public void Dispose() => _engine.Dispose();

    private void OnAdvice(AdviceEvent a)
    {
        // Non-interactive (redirected) fallback: a plain timestamped line, mirroring the panel item.
        Narrated?.Invoke($"[grey]{a.At:HH:mm:ss}[/] [{a.Colour}]coach {a.Glyph}[/] {a.Text.EscapeMarkup()}");
        lock (_lock)
        {
            _advice.Add(new Advice(a.At, a.Colour, a.Glyph, a.Text));
            if (_advice.Count > MaxAdvice)
            {
                _advice.RemoveRange(0, _advice.Count - MaxAdvice);
            }
        }
        Changed?.Invoke();
    }

    private void OnActivity(CoachActivity activity)
    {
        lock (_lock) { _activity = Present(activity); }
        Changed?.Invoke();
    }

    /// <summary>One place maps a coach activity to its panel presentation (icon text + colour).</summary>
    private static (string Text, string Colour) Present(CoachActivity activity) => activity switch
    {
        CoachActivity.Thinking => ("◍ Thinking…", "magenta"),
        CoachActivity.Quiet => ("○ Considered, nothing to add", "grey"),
        _ => ("○ Listening", "grey"),
    };

    public IRenderable RenderPanel()
    {
        lock (_lock)
        {
            return new Panel(BuildAdvice())
                .Header($"[magenta] coach [/]{CoachActivityIcon()}")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();
        }
    }

    /// <summary>A small fixed block of the most recent advice; the panel sits below the transcript.
    /// Callers hold <see cref="_lock"/>.</summary>
    private IRenderable BuildAdvice()
    {
        var visible = CoachAdviceRows;
        var slice = _advice.Count > visible
            ? _advice.GetRange(_advice.Count - visible, visible)
            : _advice;

        if (slice.Count == 0)
        {
            return new Markup("[grey](no advice yet…)[/]");
        }

        var lines = slice.Select(a =>
            $"[grey]{a.At:HH:mm:ss}[/]  [{a.Colour}]{a.Glyph}[/] {a.Text.EscapeMarkup()}");
        return new Markup(string.Join("\n", lines));
    }

    /// <summary>The coach's current activity as a border icon (thinking / listening / nothing to
    /// add). Callers hold <see cref="_lock"/>.</summary>
    private string CoachActivityIcon() =>
        // Trailing space inside the span so the text does not touch the panel border (Spectre trims
        // trailing whitespace that is outside a markup span).
        $"  [{_activity.Colour}]{_activity.Text.EscapeMarkup()} [/]";
}
