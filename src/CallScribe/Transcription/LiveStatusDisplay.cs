using System.Text;
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
    private (string Text, string Colour)? _coachActivity;
    private volatile bool _running;
    private Task? _liveTask;

    // Slash-command input line (interactive mode only; all touched on the live render thread
    // except _hint, which command side-effects update under _lock).
    private readonly StringBuilder _input = new();
    private string _hint = "";
    private volatile bool _dirty = true;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Invoked when the user runs <c>/assign-name "label" "name"</c>: rename the
    /// speaker and persist the voiceprint. Returns false when no current speaker carries that
    /// label. Set by ListenCommand; null means name assignment is unavailable (speaker-id off).</summary>
    public Func<string, string, CancellationToken, Task<bool>>? OnAssignName { get; set; }

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
        _dirty = true;
    }

    /// <summary>Set the coach activity line shown at the top of the coach panel (e.g. thinking,
    /// listening, considered-nothing-to-add). Presentation hints are passed in as primitives so
    /// this class stays independent of the coach namespace, like <see cref="PrintAdvice"/>.</summary>
    public void SetCoachActivity(string text, string colour)
    {
        lock (_lock) _coachActivity = (text, colour);
        _dirty = true;
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
        _dirty = true;
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
        _dirty = true;
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
                // This thread is the sole owner of the console: it both reads keys and repaints,
                // which keeps us within Spectre's "Live is single-threaded" rule. Poll fast for
                // responsive typing, but only repaint when something changed (or the clock ticks)
                // to avoid flicker.
                var lastSecond = -1;
                while (_running)
                {
                    DrainKeys();
                    var second = (int)(DateTime.Now - _start).TotalSeconds;
                    if (_dirty || second != lastSecond)
                    {
                        _dirty = false;
                        lastSecond = second;
                        ctx.UpdateTarget(BuildDashboard());
                    }
                    Thread.Sleep(33);
                }
                // One final frame so the last captions stay on screen after stop.
                ctx.UpdateTarget(BuildDashboard());
            });
    }

    private void DrainKeys()
    {
        try
        {
            while (Console.KeyAvailable) HandleKey(Console.ReadKey(intercept: true));
        }
        catch { /* KeyAvailable can throw on some hosts; the loop is interactive-only anyway */ }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Submit();
                break;
            case ConsoleKey.Escape:
                RequestStop();
                break;
            case ConsoleKey.Backspace:
                if (_input.Length > 0) _input.Remove(_input.Length - 1, 1);
                break;
            case ConsoleKey.Tab:
                var completed = SlashCommand.ApplyTab(_input.ToString(), CurrentLabels());
                _input.Clear();
                _input.Append(completed);
                break;
            default:
                if (!char.IsControl(key.KeyChar)) _input.Append(key.KeyChar);
                break;
        }
        _dirty = true;
    }

    private void Submit()
    {
        var line = _input.ToString().Trim();
        _input.Clear();
        if (line.Length == 0) return;

        var (cmd, args) = SlashCommand.ParseCommandLine(line);
        switch (cmd.ToLowerInvariant())
        {
            case "/stop":
                RequestStop();
                break;
            case "/help":
                SetHint(SlashCommand.HelpText);
                break;
            case "/speakers":
                var labels = CurrentLabels();
                SetHint(labels.Count > 0 ? "Speakers: " + string.Join(", ", labels) : "No far-side speakers yet.");
                break;
            case "/assign-name":
            case "/rename":
                HandleAssign(args);
                break;
            default:
                SetHint($"Unknown command '{cmd}'. Try /help.");
                break;
        }
    }

    private void HandleAssign(string[] args)
    {
        if (args.Length < 2)
        {
            SetHint("Usage: /assign-name \"<current label>\" \"<name>\"");
            return;
        }

        var label = args[0];
        var name = args[1];
        var callback = OnAssignName;
        if (callback == null)
        {
            SetHint("Speaker identification is off — can't assign names this session.");
            return;
        }

        SetHint($"Assigning {name}…");
        // Run off the render thread so the enroll DB call doesn't freeze the dashboard.
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await callback(label, name, CancellationToken.None).ConfigureAwait(false); }
            catch { ok = false; }

            if (ok)
            {
                RelabelCaptions(label, name);
                SetHint($"Renamed {label} → {name}.");
            }
            else
            {
                SetHint($"No current speaker labelled \"{label}\".");
            }
        });
    }

    /// <summary>Wait for the user to end the session: /stop or Esc in interactive mode, else a
    /// line on stdin (the redirected fallback). Replaces ListenCommand's own Console.ReadLine.</summary>
    public Task WaitForStopAsync(CancellationToken ct) =>
        _interactive ? _stop.Task.WaitAsync(ct) : Task.Run(() => { Console.ReadLine(); }, ct);

    private void RequestStop() => _stop.TrySetResult();

    private void SetHint(string text)
    {
        lock (_lock) _hint = text;
        _dirty = true;
    }

    /// <summary>Distinct far-side speaker labels currently in the transcript (for autocomplete
    /// and /speakers); excludes the user's own "Me" track.</summary>
    private List<string> CurrentLabels()
    {
        lock (_lock)
        {
            return [.. _captions.Select(c => c.Label)
                .Where(l => l != LiveCaptionEngine.MeLabel)
                .Distinct()];
        }
    }

    private void RelabelCaptions(string oldLabel, string newLabel)
    {
        lock (_lock)
        {
            for (var i = 0; i < _captions.Count; i++)
            {
                if (_captions[i].Label == oldLabel)
                {
                    _captions[i] = _captions[i] with { Label = newLabel };
                }
            }
        }
        _dirty = true;
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

            // Input line + a hint line: live autocomplete while typing a command, else the last
            // command result, else the default reminder. (Computed under the lock we already hold.)
            var labelList = _captions.Select(c => c.Label)
                .Where(l => l != LiveCaptionEngine.MeLabel).Distinct().ToList();
            var live = SlashCommand.Complete(_input.ToString(), labelList);
            var hintText = _input.Length > 0 && live.Count > 0
                ? string.Join("   ", live)
                : _hint.Length > 0
                    ? _hint
                    : $"/help for commands · /stop or Esc to finish · model {_model}";
            var footer = new Rows(
                new Markup($"[grey]>[/] {_input.ToString().EscapeMarkup()}[grey]▌[/]"),
                new Markup($"[grey]{hintText.EscapeMarkup()}[/]"));

            IRenderable body = transcript;
            if (_showAdvice)
            {
                var advice = new Panel(new Rows(BuildCoachActivity(), BuildAdvice()))
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

    /// <summary>The coach status line above the advice log: shows what the coach is doing now
    /// (thinking / listening / considered-nothing-to-add). Defaults to a quiet "Listening" until
    /// the engine reports otherwise, so the panel never looks dead.</summary>
    private IRenderable BuildCoachActivity()
    {
        var (text, colour) = _coachActivity ?? ("○ Listening", "grey");
        return new Markup($"[{colour}]{text.EscapeMarkup()}[/]\n");
    }

    private IRenderable BuildAdvice()
    {
        // One row tighter than the transcript to make room for the coach activity line above.
        var visible = Math.Max(3, SafeWindowHeight() - 13);
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
