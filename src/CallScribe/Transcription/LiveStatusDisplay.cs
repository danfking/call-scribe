using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CallScribe.Transcription;

public enum TrackState { Listening, Hearing, Transcribing }

/// <summary>The start-command live UI. When attached to a real console it renders a live
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
    private bool _showRpg;
    private RpgPanelState? _rpg;
    private readonly List<RpgEventLine> _rpgEvents = [];
    private (string Text, string Colour)? _coachActivity;
    private volatile bool _running;
    private Task? _liveTask;

    // Slash-command input line (interactive mode only; all touched on the live render thread
    // except _hint, which command side-effects update under _lock).
    private readonly StringBuilder _input = new();
    private string _hint = "";
    private volatile bool _dirty = true;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Slash-command registry (handlers close over this instance) and the autocomplete dropdown's
    // currently-selected candidate index.
    private readonly IReadOnlyList<SlashCommandSpec> _commands;
    private int _selectedCandidate;

    // The /ask answer overlay: null = none shown; Answer == null = the model call is in flight
    // ("Thinking…"). While shown, Esc dismisses it instead of ending the session.
    private AskState? _answer;

    /// <summary>Invoked when the user runs <c>/assign-name "label" "name"</c>: rename the
    /// speaker and persist the voiceprint. Returns false when no current speaker carries that
    /// label. Set by ListenCommand; null means name assignment is unavailable (speaker-id off).</summary>
    public Func<string, string, CancellationToken, Task<bool>>? OnAssignName { get; set; }

    /// <summary>Invoked when the user runs <c>/ask &lt;question&gt;</c>: answer a question about the
    /// live transcript. Args are (question, recent transcript text, ct) and the result is the answer
    /// to show in the overlay. Set by ListenCommand; null means Q&amp;A is unavailable this session.</summary>
    public Func<string, string, CancellationToken, Task<string>>? OnAsk { get; set; }

    // Keep memory bounded; only the tail that fits the window is ever rendered.
    private const int MaxCaptions = 500;

    // When the coach panel is shown it stacks below the transcript: it shows a small fixed block
    // of recent advice, and the transcript reserves that much vertical space for it.
    private const int CoachAdviceRows = 6;
    private const int CoachPanelRows = CoachAdviceRows + 2; // advice + panel borders (activity is in the border now)

    // The RPG area replaces the coach panel's slot below the transcript when enabled: the
    // battle-log pane takes the left two thirds, and the right third stacks one bordered card
    // per party member (overflow folded into a "+N" on the last card) plus the boss card.
    // Each card is 3 rows (header border, one content line, bottom border), so the area's
    // height tracks the party size; the log pane deepens to match.
    private const int RpgPartyPanes = 4;
    private const int RpgCardRows = 3;

    // Fixed rows the transcript leaves for everything that isn't its own content: the header rule,
    // its own borders, and the footer. The per-track cards row was removed (status moved into the
    // transcript border), so this is smaller than before.
    private const int ChromeRows = 9;

    // Width of a transcript line's "HH:mm:ss  label   " prefix, for estimating how many rows a
    // (speaker-coalesced, possibly long) entry wraps to.
    private const int TranscriptPrefixWidth = 18;

    // Cap on a coalesced entry's length: a turn keeps growing one entry until it gets this long,
    // then continues in a fresh entry (same speaker). Keeps normal turns as a single entry while
    // bounding a multi-paragraph monologue, so one entry can't grow without limit or, once it
    // exceeds the visible rows, crop the footer off screen.
    private const int MaxEntryChars = 1200;

    private readonly record struct Caption(DateTime At, string Colour, string Label, string Text);
    private readonly record struct Advice(DateTime At, string Colour, string Glyph, string Text);
    private readonly record struct RpgEventLine(DateTime At, string Colour, string Text);
    private readonly record struct AskState(string Question, string? Answer);

    public LiveStatusDisplay()
    {
        // The single source of truth for the dashboard's commands: name, usage, whether the first
        // arg is a speaker label (for completion), the handler, and aliases. Completion, highlighting,
        // help, and dispatch all derive from this, so a new command is one entry here.
        _commands =
        [
            new("/assign-name", "\"<label>\" \"<name>\"", true, HandleAssign, ["/rename"]),
            new("/ask", "<question about the transcript>", false, HandleAsk, []),
            new("/speakers", "list far-side speakers", false, _ => ShowSpeakers(), []),
            new("/help", "show commands", false, _ => ShowHelp(), []),
            new("/stop", "finish the session", false, _ => RequestStop(), []),
        ];
    }

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
    /// transcript). Call before the dashboard starts. A no-op while the RPG panel is
    /// enabled: the two contend for the same slot below the transcript, and RPG wins.</summary>
    public void EnableAdvicePanel()
    {
        lock (_lock)
        {
            if (!_showRpg) _showAdvice = true;
        }
    }

    /// <summary>Turn on the RPG panel below the transcript, replacing the coach panel (they
    /// share the slot). Call before the dashboard starts.</summary>
    public void EnableRpgPanel()
    {
        lock (_lock)
        {
            _showRpg = true;
            _showAdvice = false;
        }
    }

    /// <summary>Replace the RPG panel's party/boss snapshot. The state is a presentation-only
    /// record (see <see cref="RpgPanelState"/>) so this class stays independent of the Rpg
    /// namespace. No plain-line fallback: when output is redirected the snapshot is stored but
    /// never rendered; only the narrated events (via <see cref="PrintRpgEvent"/>) reach the
    /// stream.</summary>
    public void UpdateRpg(RpgPanelState state)
    {
        lock (_lock) _rpg = state;
        _dirty = true;
    }

    /// <summary>Add a narrated RPG event line ("Priya's question staggers the boss"). Colour is
    /// passed as a primitive, like <see cref="PrintAdvice"/>.</summary>
    public void PrintRpgEvent(DateTime at, string colour, string text)
    {
        if (!_interactive)
        {
            // Redirected: emit the plain (timestamped) line for pipes and logs. The event is
            // still recorded below so the panel state stays consistent either way.
            AnsiConsole.MarkupLine($"[grey]{at:HH:mm:ss}[/] [{colour}]rpg ▸[/] {text.EscapeMarkup()}");
        }
        lock (_lock)
        {
            _rpgEvents.Add(new RpgEventLine(at, colour, text));
            if (_rpgEvents.Count > MaxCaptions)
            {
                _rpgEvents.RemoveRange(0, _rpgEvents.Count - MaxCaptions);
            }
        }
        _dirty = true;
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
            // The live engine flushes speech in short chunks; while the same speaker keeps talking,
            // grow their existing line instead of printing a new one per chunk (much less noisy).
            // The entry keeps its original timestamp (when the turn started). A turn that runs past
            // MaxEntryChars continues in a fresh entry so one entry can't grow without bound.
            if (_captions.Count > 0 && _captions[^1].Label == label && _captions[^1].Text.Length < MaxEntryChars)
            {
                var prev = _captions[^1];
                _captions[^1] = prev with { Text = $"{prev.Text} {caption}" };
            }
            else
            {
                _captions.Add(new Caption(at, colour, label, caption));
                if (_captions.Count > MaxCaptions)
                {
                    _captions.RemoveRange(0, _captions.Count - MaxCaptions);
                }
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
                // Esc dismisses an open answer overlay first; only ends the session when none is shown.
                if (!DismissAnswer()) RequestStop();
                break;
            case ConsoleKey.UpArrow:
                MoveCandidate(-1);
                break;
            case ConsoleKey.DownArrow:
                MoveCandidate(1);
                break;
            case ConsoleKey.Tab:
                ApplySelectedCandidate();
                break;
            case ConsoleKey.Backspace:
                if (_input.Length > 0) _input.Remove(_input.Length - 1, 1);
                _selectedCandidate = 0; // candidates changed; restart selection at the top
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _input.Append(key.KeyChar);
                    _selectedCandidate = 0;
                }
                break;
        }
        _dirty = true;
    }

    /// <summary>Autocomplete candidates for the current input (commands while typing the verb, or
    /// speaker labels for a label-taking command's first argument).</summary>
    private IReadOnlyList<string> CurrentCandidates() =>
        SlashCommand.Complete(_input.ToString(), _commands, CurrentLabels());

    private void MoveCandidate(int delta)
    {
        var count = CurrentCandidates().Count;
        if (count == 0) return;
        _selectedCandidate = ((_selectedCandidate + delta) % count + count) % count; // wrap both ways
    }

    private void ApplySelectedCandidate()
    {
        var candidates = CurrentCandidates();
        if (candidates.Count == 0) return;
        var idx = Math.Clamp(_selectedCandidate, 0, candidates.Count - 1);
        var completed = SlashCommand.ApplyCompletion(_input.ToString(), candidates[idx]);
        _input.Clear();
        _input.Append(completed);
        _selectedCandidate = 0;
    }

    private void Submit()
    {
        var line = _input.ToString().Trim();
        _input.Clear();
        _selectedCandidate = 0;
        if (line.Length == 0) return;

        var (cmd, args) = SlashCommand.ParseCommandLine(line);
        var spec = SlashCommand.Match(cmd, _commands);
        if (spec == null)
        {
            SetHint($"Unknown command '{cmd}'. Try /help.");
            return;
        }
        spec.Handler(args);
    }

    private void ShowSpeakers()
    {
        var labels = CurrentLabels();
        SetHint(labels.Count > 0 ? "Speakers: " + string.Join(", ", labels) : "No far-side speakers yet.");
    }

    private void ShowHelp() => SetHint(SlashCommand.Help(_commands));

    /// <summary>Answer a question about the live transcript: show a "thinking" overlay, run the
    /// model call off the render thread, then fill in the answer (unless the overlay was dismissed or
    /// a newer question replaced it). The transcript context is built here so the display owns the
    /// captions; ListenCommand's callback just runs the model.</summary>
    private void HandleAsk(string[] args)
    {
        var question = string.Join(" ", args).Trim();
        if (question.Length == 0)
        {
            SetHint("Usage: /ask <question about the transcript>");
            return;
        }

        var callback = OnAsk;
        if (callback == null)
        {
            SetHint("Q&A is unavailable this session.");
            return;
        }

        var transcript = RecentTranscriptText();
        lock (_lock) _answer = new AskState(question, null); // null answer => thinking
        _dirty = true;

        _ = Task.Run(async () =>
        {
            string answer;
            try { answer = await callback(question, transcript, CancellationToken.None).ConfigureAwait(false); }
            catch { answer = "Sorry, that question could not be answered (the model call failed)."; }

            lock (_lock)
            {
                // Only fill in if this question's overlay is still the one showing (not dismissed,
                // not superseded by a newer /ask).
                if (_answer is { } a && a.Question == question && a.Answer == null)
                {
                    _answer = a with { Answer = answer };
                }
            }
            _dirty = true;
        });
    }

    /// <summary>Dismiss the answer overlay if one is shown; returns whether there was one.</summary>
    private bool DismissAnswer()
    {
        lock (_lock)
        {
            if (_answer == null) return false;
            _answer = null;
        }
        _dirty = true;
        return true;
    }

    /// <summary>Recent transcript text (speaker: line) for the Q&A context.</summary>
    private string RecentTranscriptText()
    {
        const int maxLines = 40;
        lock (_lock)
        {
            var start = Math.Max(0, _captions.Count - maxLines);
            return string.Join("\n", _captions.Skip(start).Select(c => $"{c.Label}: {c.Text}"));
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

            // Per-track status now lives in the transcript's top border (one icon per track that
            // changes with its state) instead of a separate cards row, to save vertical space.
            var transcript = new Panel(BuildTranscript())
                .Header($"[grey] transcript [/]   {TrackIcons()}")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();

            // Input line (command word colour-highlighted) plus, below it, a selectable autocomplete
            // dropdown while typing a command, else the last command result or the default reminder.
            var labelList = _captions.Select(c => c.Label)
                .Where(l => l != LiveCaptionEngine.MeLabel).Distinct().ToList();
            var candidates = SlashCommand.Complete(_input.ToString(), _commands, labelList);
            var inputLine = new Markup($"[grey]>[/] {SlashCommand.Highlight(_input.ToString(), _commands)}[grey]▌[/]");

            IRenderable secondRow;
            if (candidates.Count > 0)
            {
                secondRow = BuildCandidateDropdown(candidates);
            }
            else
            {
                var hintText = _hint.Length > 0
                    ? _hint
                    : $"/help for commands · /stop or Esc to finish · model {_model}";
                secondRow = new Markup($"[grey]{hintText.EscapeMarkup()}[/]");
            }
            var footer = new Rows(inputLine, secondRow);

            IRenderable body = transcript;
            if (_answer is { } ask)
            {
                // An /ask answer overlay takes the slot below the transcript (over the coach or
                // the RPG panel) until dismissed with Esc, so the layout stays stable.
                body = new Rows(transcript, BuildAnswerPanel(ask));
            }
            else if (_showRpg)
            {
                body = new Rows(transcript, BuildRpg());
            }
            else if (_showAdvice)
            {
                // Activity line, a blank separator, then the advice log — spacing via Rows only.
                // The coach's activity (thinking / listening / nothing to add) is shown as an icon in
                // the panel border; the content is just the advice log.
                var advice = new Panel(BuildAdvice())
                    .Header($"[magenta] coach [/]{CoachActivityIcon()}")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Grey)
                    .Expand();
                // Stack the coach below the transcript (full width), so the transcript keeps the
                // full line width for long captions instead of being squeezed into half.
                body = new Rows(transcript, advice);
            }

            return new Rows(header, body, footer);
        }
    }

    /// <summary>The /ask answer overlay panel: the question, then the answer (or "Thinking…" while
    /// the model runs), with an Esc-to-dismiss hint. Coach-coloured so it reads as assistant output.</summary>
    private IRenderable BuildAnswerPanel(AskState ask)
    {
        var answer = ask.Answer is { } text
            ? new Markup($"[white]{text.EscapeMarkup()}[/]")
            : new Markup("[grey]Thinking…[/]");
        var content = new Rows(
            new Markup($"[grey]Q:[/] {ask.Question.EscapeMarkup()}"),
            new Markup(""),
            answer,
            new Markup(""),
            new Markup("[grey]Esc to dismiss[/]"));
        return new Panel(content)
            .Header("[cyan] ask [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Expand();
    }

    /// <summary>Per-track status icons for the transcript's top border: a glyph that changes with
    /// each track's state, in the track's colour. Replaces the old per-track cards row.</summary>
    private string TrackIcons()
    {
        var parts = _order.Select(t =>
        {
            var glyph = _states.GetValueOrDefault(t.Label) switch
            {
                TrackState.Transcribing => "▶",
                TrackState.Hearing => "◐",
                _ => "○",
            };
            // Trailing space kept INSIDE the grey span: Spectre trims trailing whitespace that sits
            // outside a markup span, so the last icon would otherwise touch the panel border.
            return $"[{t.Colour}]{glyph}[/] [grey]{t.Label.EscapeMarkup()} [/]";
        });
        return string.Join("  ", parts);
    }

    /// <summary>The coach's current activity as a border icon (thinking / listening / nothing to
    /// add), or empty before the first state is set.</summary>
    private string CoachActivityIcon() =>
        // Trailing space inside the span so the text does not touch the panel border (Spectre trims
        // trailing whitespace that is outside a markup span).
        _coachActivity is { } a ? $"  [{a.Colour}]{a.Text.EscapeMarkup()} [/]" : "";

    /// <summary>The autocomplete dropdown: a short vertical list of candidates with the selected one
    /// highlighted. Windows to the selection when there are more candidates than fit.</summary>
    private IRenderable BuildCandidateDropdown(IReadOnlyList<string> candidates)
    {
        const int maxRows = 6;
        var sel = Math.Clamp(_selectedCandidate, 0, candidates.Count - 1);
        var start = candidates.Count <= maxRows ? 0 : Math.Clamp(sel - maxRows + 1, 0, candidates.Count - maxRows);

        var rows = new List<IRenderable>();
        for (var i = start; i < Math.Min(candidates.Count, start + maxRows); i++)
        {
            var text = candidates[i].EscapeMarkup();
            rows.Add(i == sel
                ? new Markup($"  [black on cyan] {text} [/]")
                : new Markup($"  [grey]{text}[/]"));
        }
        if (candidates.Count > 1)
        {
            rows.Add(new Markup("  [grey]↑↓ select · Tab to apply[/]"));
        }
        return new Rows(rows);
    }

    private IRenderable BuildTranscript()
    {
        // Show the newest entries that fit, leaving room for the header, cards, borders, footer,
        // and (when shown) the coach panel below. Entries are speaker-coalesced so they can wrap
        // to several rows; budget by estimated rendered rows (not entry count) so one long turn
        // can't push the footer off screen.
        var reserved = _answer != null ? CoachPanelRows
            : _showRpg ? RpgReservedRows()
            : _showAdvice ? CoachPanelRows
            : 0;
        var rowBudget = Math.Max(3, SafeWindowHeight() - ChromeRows - reserved);
        var lineWidth = Math.Max(20, SafeWindowWidth() - 4); // minus panel border/padding

        var chosen = new List<Caption>();
        var rows = 0;
        for (var i = _captions.Count - 1; i >= 0; i--)
        {
            var estRows = WrappedRows(TranscriptPrefixWidth + _captions[i].Text.Length, lineWidth);
            if (chosen.Count > 0 && rows + estRows > rowBudget) break;
            chosen.Add(_captions[i]);
            rows += estRows;
        }
        chosen.Reverse();

        if (chosen.Count == 0)
        {
            return new Markup("[grey](waiting for audio…)[/]");
        }

        var lines = chosen.Select(c =>
            $"[grey]{c.At:HH:mm:ss}[/]  [{c.Colour}]{c.Label,-6}[/] {c.Text.EscapeMarkup()}");
        return new Markup(string.Join("\n", lines));
    }

    /// <summary>Estimated number of wrapped rows for a line of <paramref name="chars"/> visible
    /// characters at <paramref name="lineWidth"/> columns (at least one row).</summary>
    private static int WrappedRows(int chars, int lineWidth) => Math.Max(1, (chars + lineWidth - 1) / lineWidth);

    private IRenderable BuildAdvice()
    {
        // A small fixed block of the most recent advice; the panel sits below the transcript.
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

    /// <summary>Rows the RPG area needs below the transcript: 3 per card (party, capped, plus
    /// the boss), tracked dynamically because the party grows as people speak. Callers hold
    /// <see cref="_lock"/> (this reads <see cref="_rpg"/>).</summary>
    private int RpgReservedRows()
    {
        if (_rpg is not { } state) return 3; // the pre-snapshot placeholder panel
        var cards = Math.Min(state.Party.Count, RpgPartyPanes) + 1; // party + boss
        return cards * RpgCardRows + (state.Boss.MobNames.Count > 0 ? 1 : 0);
    }

    /// <summary>The RPG area: the battle-log pane on the left two thirds, and the right third
    /// stacking one card per party member plus the boss card. The log's depth follows the
    /// stack's so the two columns bottom out together.</summary>
    private IRenderable BuildRpg()
    {
        if (_rpg is not { } state)
        {
            return new Panel(new Markup("[grey](the party assembles…)[/]"))
                .Header("[red] boss fight [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();
        }

        // A fixed 2:1 split; Grid columns take explicit widths, so compute them from the
        // window (minus a safety margin for the grid's own bookkeeping). The right column
        // never drops below the width where minimum bars still fit on one line: a wrapped
        // card grows a row and breaks the log/stack height sync.
        var total = Math.Max(60, SafeWindowWidth() - 2);
        var rightWidth = Math.Max(34, total / 3);
        var leftWidth = total - rightWidth - 1;

        var cards = new List<IRenderable>();
        var visible = Math.Min(state.Party.Count, RpgPartyPanes);
        // Inside a card: column width minus borders and padding. The card line's fixed text
        // ("HP [] nn/nn MP [] nn/nn") is 23 cells at worst, leaving the rest for two bars.
        var inner = rightWidth - 4;
        var barWidth = Math.Clamp((inner - 23) / 2, 3, 10);
        for (var i = 0; i < visible; i++)
        {
            var p = state.Party[i];
            var overflow = i == visible - 1 && state.Party.Count > visible
                ? $" [grey](+{state.Party.Count - visible})[/]"
                : "";
            var content =
                $"[grey]HP[/] {BarMarkup(p.Hp, p.MaxHp, barWidth, BarColour(p.Hp, p.MaxHp))} [grey]{p.Hp}/{p.MaxHp}[/] "
                + $"[grey]MP[/] {BarMarkup(p.Mp, p.MaxMp, barWidth, "blue")} [grey]{p.Mp}/{p.MaxMp}[/]";
            cards.Add(new Panel(new Markup(content))
                .Header($"[{p.Colour}] {p.ClassIcon} {TrimName(p.Name).EscapeMarkup()} L{p.Level} [/]{overflow}")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand());
        }

        var boss = state.Boss;
        // Brackets (2) + space + up to "999/999" (7) leaves inner - 10 cells for the fill.
        var bossBarWidth = Math.Clamp(inner - 10, 5, 40);
        var bossContent = $"{BarMarkup(boss.Hp, boss.MaxHp, bossBarWidth, "red")} [grey]{boss.Hp}/{boss.MaxHp}[/]";
        if (boss.MobNames.Count > 0)
        {
            bossContent += $"\n[grey]mobs: {TrimName(string.Join(", ", boss.MobNames), inner - 6).EscapeMarkup()}[/]";
        }
        // A text tag, not a glyph: skull/emoji glyphs are double-width in some fonts and
        // collide with the name.
        cards.Add(new Panel(new Markup(bossContent))
            .Header($"[bold red] BOSS {TrimName(boss.Name, 20).EscapeMarkup()} [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red)
            .Expand());

        // Battle-log style, no timestamps (the transcript above carries the real clock).
        // Depth matches the card stack; pad with blanks so the log pane bottoms out with it.
        var logRows = Math.Max(1, RpgReservedRows() - 2);
        var slice = _rpgEvents.Count > logRows
            ? _rpgEvents.GetRange(_rpgEvents.Count - logRows, logRows)
            : _rpgEvents;
        var logLines = slice.Select(e => $"[grey]>[/] [{e.Colour}]{e.Text.EscapeMarkup()}[/]").ToList();
        while (logLines.Count < logRows) logLines.Add("");
        var log = new Panel(new Markup(string.Join("\n", logLines)))
            .Header("[red] boss fight [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand();

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(leftWidth).Padding(0, 0, 1, 0));
        grid.AddColumn(new GridColumn().Width(rightWidth).Padding(0, 0, 0, 0));
        grid.AddRow(log, new Rows(cards));
        return grid;
    }

    /// <summary>Cap a display name so one long name (or mob list) cannot blow the row layout.</summary>
    private static string TrimName(string name, int max = 14) =>
        name.Length <= max ? name : name[..(max - 1)] + "…";

    /// <summary>A bracketed segment meter as Spectre markup: grey brackets frame the coloured
    /// fill, so the bar keeps its shape even where colour support is poor ("[[" renders a
    /// literal bracket).</summary>
    private static string BarMarkup(int value, int max, int width, string colour) =>
        $"[grey][[[/][{colour}]{AsciiBar(value, max, width)}[/][grey]]][/]";

    /// <summary>HP bar colour by remaining fraction: healthy green, hurting yellow, critical red.</summary>
    private static string BarColour(int value, int max)
    {
        var fraction = max > 0 ? value / (double)max : 0;
        return fraction > 0.5 ? "green" : fraction > 0.25 ? "yellow" : "red";
    }

    /// <summary>value/max rendered as a width-cell ASCII segment fill, e.g. "========--", the
    /// Angband/MUD meter style. Deliberately plain '='/'-': block-element glyphs are the most
    /// font-dependent characters there are (they fused into featureless rectangles in practice),
    /// while these render as discrete segments in every monospace font. A nonzero value always
    /// shows at least one segment, and a less-than-full value never shows a full bar, so
    /// "almost dead" and "almost done" stay visible at a glance.</summary>
    internal static string AsciiBar(int value, int max, int width)
    {
        if (width <= 0) return "";
        var clamped = max > 0 ? Math.Clamp(value, 0, max) : 0;
        var filled = max > 0 ? (int)Math.Round(width * clamped / (double)max) : 0;
        if (clamped > 0 && filled == 0) filled = 1;
        if (clamped < max && filled == width) filled = width - 1;
        return new string('=', filled) + new string('-', width - filled);
    }

    /// <summary>Render the RPG area to plain text at the given console width. A test seam:
    /// the live panel is interactive-only, so without this the pane layout is verifiable only
    /// by eye. Note <see cref="BuildRpg"/> sizes its columns from <see cref="SafeWindowWidth"/>
    /// (the fallback 100 under a redirected test host), not from <paramref name="width"/>.</summary>
    internal string RenderRpgToText(int width)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        lock (_lock) console.Write(BuildRpg());
        return writer.ToString();
    }

    private static int SafeWindowHeight()
    {
        try { return Console.WindowHeight; }
        catch { return 24; }
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 100; }
    }
}
