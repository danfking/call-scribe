using System.Text;
using CallScribe.Transcription;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CallScribe.Rpg;

/// <summary>The boss-fight panel's rendering and its snapshot state, split out from
/// <see cref="RpgModule"/> so it can be rendered without a running engine (the layout tests drive
/// it directly). This is the code that used to live in LiveStatusDisplay; it holds only
/// presentation-ready records and never touches the game logic. State is written on the engine's
/// loop thread (via the module) and read on the render thread, so everything is guarded by
/// <see cref="_lock"/>.</summary>
public sealed class RpgPanel
{
    // The battle-log pane takes the left two thirds, the right third stacks one 3-row card per party
    // member plus the boss card, so the area's height tracks the party size.
    private const int RpgPartyPanes = 4;
    private const int RpgCardRows = 3;
    private const int MaxEvents = 500;
    private static readonly TimeSpan RpgFlashDuration = TimeSpan.FromSeconds(1.5);

    private readonly Lock _lock = new();
    private RpgPanelState? _rpg;
    private readonly List<RpgEventLine> _rpgEvents = [];
    // When each card's numbers last moved, for the brief border flash that points at the change.
    private readonly Dictionary<string, DateTime> _rpgCardChanged = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _rpgBossChanged;

    private readonly record struct RpgEventLine(DateTime At, string Colour, string Text);

    /// <summary>Replace the party/boss snapshot, stamping the cards whose numbers moved so they
    /// flash. The first snapshot flashes nothing; a newly joined member counts as a change.</summary>
    public void Update(RpgPanelState state)
    {
        lock (_lock)
        {
            if (_rpg is { } prev)
            {
                var now = DateTime.Now;
                var previous = prev.Party.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var row in state.Party)
                {
                    if (!previous.TryGetValue(row.Name, out var old)
                        || old.Hp != row.Hp || old.Mp != row.Mp || old.Level != row.Level)
                    {
                        _rpgCardChanged[row.Name] = now;
                    }
                }
                if (prev.Boss.Hp != state.Boss.Hp || prev.Boss.MaxHp != state.Boss.MaxHp)
                {
                    _rpgBossChanged = now;
                }
            }
            _rpg = state;
        }
    }

    /// <summary>Add a narrated battle-log line ("Priya's question staggers the boss").</summary>
    public void AddEvent(DateTime at, string colour, string text)
    {
        lock (_lock)
        {
            _rpgEvents.Add(new RpgEventLine(at, colour, text));
            if (_rpgEvents.Count > MaxEvents)
            {
                _rpgEvents.RemoveRange(0, _rpgEvents.Count - MaxEvents);
            }
        }
    }

    /// <summary>Whether a card's change flash is currently active (test seam: border colours are
    /// invisible in the colourless test render).</summary>
    internal bool CardChangedRecently(string name)
    {
        lock (_lock) { return DateTime.Now - _rpgCardChanged.GetValueOrDefault(name) < RpgFlashDuration; }
    }

    internal bool BossChangedRecently()
    {
        lock (_lock) { return DateTime.Now - _rpgBossChanged < RpgFlashDuration; }
    }

    public int ReserveRows()
    {
        lock (_lock) { return ReservedRowsLocked(); }
    }

    /// <summary>Rows the RPG area needs below the transcript: 3 per card (party, capped, plus the
    /// boss), tracked dynamically because the party grows as people speak. Callers hold
    /// <see cref="_lock"/>.</summary>
    private int ReservedRowsLocked()
    {
        if (_rpg is not { } state) return 3; // the pre-snapshot placeholder panel
        var cards = Math.Min(state.Party.Count, RpgPartyPanes) + 1; // party + boss
        return cards * RpgCardRows + (state.Boss.MobNames.Count > 0 ? 1 : 0);
    }

    public IRenderable Render()
    {
        lock (_lock) { return BuildRpg(); }
    }

    /// <summary>The RPG area: the battle-log pane on the left two thirds, and the right third
    /// stacking one card per party member plus the boss card. The log's depth follows the stack's
    /// so the two columns bottom out together. Callers hold <see cref="_lock"/>.</summary>
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

        // A fixed 2:1 split; Grid columns take explicit widths, so compute them from the window
        // (minus a safety margin for the grid's own bookkeeping). The right column never drops
        // below the width where minimum bars still fit on one line: a wrapped card grows a row and
        // breaks the log/stack height sync.
        var total = Math.Max(60, SafeWindowWidth() - 2);
        var rightWidth = Math.Max(34, total / 3);
        var leftWidth = total - rightWidth - 1;

        // A just-changed card flashes its border (member colour; white for the boss) for a moment,
        // then settles back: the stack order is stable, so the flash is the pointer.
        var now = DateTime.Now;
        bool Flashing(DateTime at) => now - at < RpgFlashDuration;

        var cards = new List<IRenderable>();
        var visible = Math.Min(state.Party.Count, RpgPartyPanes);
        // Inside a card: column width minus borders and padding. The numbers overlay the bars, so
        // the fixed text ("HP || MP ||") is just 11 cells; the bars absorb the rest. The floor keeps
        // a worst-case "999/999" reading inside its bar.
        var inner = rightWidth - 4;
        var barWidth = Math.Clamp((inner - 11) / 2, 9, 18);

        var boss = state.Boss;
        // The boss card leads the stack (the enemy tops the battle screen). Its bar spans the card;
        // the reading overlays it too. A text tag, not a glyph: skull/emoji glyphs are double-width
        // in some fonts and collide with the name.
        var bossBarWidth = Math.Clamp(inner - 2, 9, 60);
        var bossContent = BarMarkup(boss.Hp, boss.MaxHp, bossBarWidth, "red");
        if (boss.MobNames.Count > 0)
        {
            bossContent += $"\n[grey]mobs: {TrimName(string.Join(", ", boss.MobNames), inner - 6).EscapeMarkup()}[/]";
        }
        cards.Add(new Panel(new Markup(bossContent))
            .Header($"[bold red] BOSS {TrimName(boss.Name, 20).EscapeMarkup()} [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Flashing(_rpgBossChanged) ? Color.White : Color.Red)
            .Expand());

        for (var i = 0; i < visible; i++)
        {
            var p = state.Party[i];
            var overflow = i == visible - 1 && state.Party.Count > visible
                ? $" [grey](+{state.Party.Count - visible})[/]"
                : "";
            var content =
                $"[grey]HP[/] {BarMarkup(p.Hp, p.MaxHp, barWidth, BarColour(p.Hp, p.MaxHp))} "
                + $"[grey]MP[/] {BarMarkup(p.Mp, p.MaxMp, barWidth, "blue")}";
            var border = Flashing(_rpgCardChanged.GetValueOrDefault(p.Name))
                ? Style.Parse(p.Colour).Foreground
                : Color.Grey;
            cards.Add(new Panel(new Markup(content))
                .Header($" {BadgeMarkup(p.Level, p.XpProgress, p.Colour)}[{p.Colour}] {TrimName(p.Name).EscapeMarkup()} [/]{overflow}")
                .Border(BoxBorder.Rounded)
                .BorderColor(border)
                .Expand());
        }

        // Battle-log style, no timestamps (the transcript above carries the real clock). Depth
        // matches the card stack; pad with blanks so the log pane bottoms out with it.
        var logRows = Math.Max(1, ReservedRowsLocked() - 2);
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

    /// <summary>A framed meter as Spectre markup: the fill is a background-colour wash over space
    /// cells (terminal-drawn rectangles, so it renders uniformly in any font, unlike glyphs), the
    /// empty portion is bare, and grey '|' end caps mark the extent. The reading overlays the
    /// middle: digits over filled cells render on the fill colour, digits over empty cells render
    /// bold white on none, so the fill boundary stays visible even when it lands mid-label.</summary>
    private static string BarMarkup(int value, int max, int width, string colour)
    {
        var label = $"{Math.Max(0, value)}/{Math.Max(0, max)}";
        if (width <= label.Length) return $"[bold white]{label.EscapeMarkup()}[/]";

        var filled = FilledCells(value, max, width);
        var start = (width - label.Length) / 2;
        // Black text carries best on the bright fills; blue is dark enough to need white.
        var textOnFill = colour == "blue" ? "white" : "black";
        var markup = new StringBuilder("[grey]|[/]");
        AppendCells(0, start);
        AppendLabel();
        AppendCells(start + label.Length, width);
        markup.Append("[grey]|[/]");
        return markup.ToString();

        void AppendCells(int from, int to)
        {
            var fillTo = Math.Clamp(filled, from, to);
            // Spaces stay inside markup spans: Spectre trims whitespace that sits outside one.
            if (fillTo > from) markup.Append($"[on {colour}]{new string(' ', fillTo - from)}[/]");
            if (to > fillTo) markup.Append($"[grey]{new string(' ', to - fillTo)}[/]");
        }

        void AppendLabel()
        {
            // Split the reading at the fill boundary so each side takes its own styling.
            var overFill = Math.Clamp(filled - start, 0, label.Length);
            if (overFill > 0) markup.Append($"[bold {textOnFill} on {colour}]{label[..overFill]}[/]");
            if (overFill < label.Length) markup.Append($"[bold white]{label[overFill..]}[/]");
        }
    }

    /// <summary>The level badge: the level number on a small background chip that doubles as an XP
    /// meter, washing over left-to-right in the member's colour as progress toward the next level
    /// accrues. The unfilled portion keeps a dim grey background so the badge shape reads even at
    /// 0%. Same split-text treatment as the bars, so the fill boundary shows through the digits.</summary>
    private static string BadgeMarkup(int level, double xpProgress, string colour)
    {
        var text = $" {level} ";
        var filled = (int)Math.Round(text.Length * Math.Clamp(xpProgress, 0, 1));
        if (filled == text.Length && xpProgress < 1) filled = text.Length - 1;

        var textOnFill = colour == "blue" ? "white" : "black";
        var markup = new StringBuilder();
        if (filled > 0) markup.Append($"[bold {textOnFill} on {colour}]{text[..filled]}[/]");
        if (filled < text.Length) markup.Append($"[bold white on grey19]{text[filled..]}[/]");
        return markup.ToString();
    }

    /// <summary>HP bar colour by remaining fraction: healthy green, hurting yellow, critical red.</summary>
    private static string BarColour(int value, int max)
    {
        var fraction = max > 0 ? value / (double)max : 0;
        return fraction > 0.5 ? "green" : fraction > 0.25 ? "yellow" : "red";
    }

    /// <summary>How many of a width-cell meter's cells are filled for value/max. A nonzero value
    /// always fills at least one cell, and a less-than-full value never fills the whole bar, so
    /// "almost dead" and "almost done" stay visible at a glance.</summary>
    internal static int FilledCells(int value, int max, int width)
    {
        if (width <= 0) return 0;
        var clamped = max > 0 ? Math.Clamp(value, 0, max) : 0;
        var filled = max > 0 ? (int)Math.Round(width * clamped / (double)max) : 0;
        if (clamped > 0 && filled == 0) filled = 1;
        if (clamped < max && filled == width) filled = width - 1;
        return filled;
    }

    /// <summary>Render the RPG area to plain text at the given console width. A test seam: the live
    /// panel is interactive-only. Note <see cref="BuildRpg"/> sizes its columns from
    /// <see cref="SafeWindowWidth"/> (the fallback 100 under a redirected test host), not from
    /// <paramref name="width"/>.</summary>
    internal string RenderToText(int width)
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
        lock (_lock) { console.Write(BuildRpg()); }
        return writer.ToString();
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 100; }
    }
}
