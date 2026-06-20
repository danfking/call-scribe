using Spectre.Console;
using Spectre.Console.Rendering;

namespace CallScribe.Transcription;

public enum TrackState { Listening, Hearing, Transcribing }

/// <summary>The listen-mode UI. When attached to a real console it renders a live
/// dashboard (a header with elapsed time, a card per track showing its state, and
/// a transcript panel that updates in place). When output is redirected it falls
/// back to plain caption lines so pipes and logs still work.</summary>
public sealed class LiveStatusDisplay : IDisposable
{
    private readonly bool _interactive = ConsoleIsUsable();
    private readonly Lock _lock = new();
    private readonly List<(string Label, string Colour)> _order = [];
    private readonly Dictionary<string, TrackState> _states = [];
    private readonly List<Caption> _captions = [];
    private readonly List<Advice> _advice = [];
    private readonly DateTime _start = DateTime.Now;
    private string _model = "";
    private bool _started;
    private bool _showAdvice;
    private volatile bool _running;
    private Task? _liveTask;

    // Keep memory bounded; only the tail that fits the window is ever rendered.
    private const int MaxCaptions = 500;

    private readonly record struct Caption(DateTime At, string Colour, string Label, string Text);
    private readonly record struct Advice(DateTime At, string Colour, string Glyph, string Text);

    private static bool ConsoleIsUsable()
    {
        if (Console.IsOutputRedirected) return false;
        try { _ = Console.WindowWidth; return true; }
        catch { return false; }
    }

    /// <summary>Footer detail shown on the dashboard (the live model name).</summary>
    public void Configure(string model)
    {
        lock (_lock) _model = model;
    }

    public void Register(string label, string colour)
    {
        lock (_lock)
        {
            _order.Add((label, colour));
            _states[label] = TrackState.Listening;
        }
        EnsureStarted();
    }

    public void SetState(string label, TrackState state)
    {
        lock (_lock) _states[label] = state;
    }

    /// <summary>Turn on the coach advice column (rendered to the right of the
    /// transcript). Call before the dashboard starts.</summary>
    public void EnableAdvicePanel()
    {
        lock (_lock) _showAdvice = true;
    }

    /// <summary>Add a coach advice item. Presentation hints (colour, glyph) are passed
    /// in so this class stays independent of the coach namespace.</summary>
    public void PrintAdvice(DateTime at, string colour, string glyph, string text)
    {
        if (!_interactive)
        {
            AnsiConsole.MarkupLine($"[grey]{at:HH:mm:ss}[/] [{colour}]coach {glyph}[/] {text.EscapeMarkup()}");
            return;
        }
        lock (_lock)
        {
            _advice.Add(new Advice(at, colour, glyph, text));
            if (_advice.Count > MaxCaptions)
            {
                _advice.RemoveRange(0, _advice.Count - MaxCaptions);
            }
        }
    }

    public void PrintCaption(DateTime at, string colour, string label, string caption)
    {
        if (!_interactive)
        {
            // Redirected: keep the plain line stream so pipes and logs still work.
            AnsiConsole.MarkupLine($"[grey]{at:HH:mm:ss}[/] [{colour}]{label,-6}[/] {caption.EscapeMarkup()}");
            return;
        }
        lock (_lock)
        {
            _captions.Add(new Caption(at, colour, label, caption));
            if (_captions.Count > MaxCaptions)
            {
                _captions.RemoveRange(0, _captions.Count - MaxCaptions);
            }
        }
    }

    /// <summary>Stop the live loop so the shell prompt (and any later output) lands cleanly.</summary>
    public void Shutdown()
    {
        if (!_running) return;
        _running = false;
        try { _liveTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
    }

    public void Dispose() => Shutdown();

    private void EnsureStarted()
    {
        if (!_interactive) return;
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            _running = true;
        }
        _liveTask = Task.Run(RunLive);
    }

    private void RunLive()
    {
        AnsiConsole.Live(BuildDashboard())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                while (_running)
                {
                    ctx.UpdateTarget(BuildDashboard());
                    Thread.Sleep(250);
                }
                // One final frame so the last captions stay on screen after stop.
                ctx.UpdateTarget(BuildDashboard());
            });
    }

    private IRenderable BuildDashboard()
    {
        lock (_lock)
        {
            var elapsed = DateTime.Now - _start;
            var header = new Rule($"[bold]call-scribe[/]  [red]●[/] [grey]rec[/]  [grey]{elapsed:hh\\:mm\\:ss}[/]")
            {
                Justification = Justify.Left,
                Style = Style.Parse("grey"),
            };

            var cards = new Columns(_order.Select(BuildCard).ToArray()) { Expand = true };

            var transcript = new Panel(BuildTranscript())
                .Header("[grey] transcript [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();

            var footer = new Markup(
                $"[grey]Enter[/] stop & transcribe     [grey]model[/] {_model.EscapeMarkup()}");

            IRenderable body = transcript;
            if (_showAdvice)
            {
                var advice = new Panel(BuildAdvice())
                    .Header("[magenta] coach [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Grey)
                    .Expand();
                body = new Columns(transcript, advice) { Expand = true };
            }

            return new Rows(header, cards, body, footer);
        }
    }

    private IRenderable BuildCard((string Label, string Colour) track)
    {
        var (glyph, word) = _states.GetValueOrDefault(track.Label) switch
        {
            TrackState.Transcribing => ("▶", "transcribing"),
            TrackState.Hearing => ("◐", "hearing audio"),
            _ => ("○", "listening"),
        };
        var content = new Markup($"  [{track.Colour}]{glyph}[/]  [grey]{word}[/]");
        return new Panel(content)
            .Header($"[{track.Colour}] {track.Label} [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand();
    }

    private IRenderable BuildTranscript()
    {
        // Show the tail that fits, leaving room for the header, cards, borders, footer.
        var visible = Math.Max(3, SafeWindowHeight() - 12);
        var slice = _captions.Count > visible
            ? _captions.GetRange(_captions.Count - visible, visible)
            : _captions;

        if (slice.Count == 0)
        {
            return new Markup("[grey](waiting for audio…)[/]");
        }

        var lines = slice.Select(c =>
            $"[grey]{c.At:HH:mm:ss}[/]  [{c.Colour}]{c.Label,-6}[/] {c.Text.EscapeMarkup()}");
        return new Markup(string.Join("\n", lines));
    }

    private IRenderable BuildAdvice()
    {
        var visible = Math.Max(3, SafeWindowHeight() - 12);
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

    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; }
        catch { return 24; }
    }
}
