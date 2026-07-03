using System.Threading.Channels;
using CallScribe.Transcription;

namespace CallScribe.Rpg;

/// <summary>Runs the meeting-as-boss-fight game over the live caption stream. Same threading
/// model as the coach engine: <see cref="Observe"/> only enqueues (safe from caption worker
/// threads), and a single background task drains the inbox, so the <see cref="GameState"/> is
/// mutated on one task and needs no locking. A periodic ticker feeds wall-clock inputs through
/// the same inbox (silence, mana regen), keeping every mutation serialized.</summary>
public sealed class RpgEngine : IDisposable
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(5);

    // Stable per-member row colours; self is always cyan to match the Me track.
    private static readonly string[] PartyPalette = ["yellow", "magenta", "green", "blue", "orange3"];

    private readonly RpgRules _rules;
    private readonly string? _selfName;
    private readonly GameState _state;
    private readonly Channel<Input> _inbox =
        Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Task _ticker;
    private bool _vanquishAnnounced;

    // A null caption is a wall-clock tick. Captions carry their own timestamp so replayed
    // meetings score against the scripted clock, not the processing time.
    private readonly record struct Input(CaptionEvent? Caption, DateTime Now);

    /// <summary>A fresh presentation snapshot after any state change. Fires on the loop task.</summary>
    public event Action<RpgPanelState>? StateChanged;

    /// <summary>A narrated event line for the panel's log, as (at, colour, text) primitives.
    /// Fires on the loop task, never on the caption thread.</summary>
    public event Action<DateTime, string, string>? EventEmitted;

    public RpgEngine(RpgRules rules, string bossName = "The Meeting", string? selfName = null)
    {
        _rules = rules;
        _selfName = selfName;
        _state = new GameState(bossName);
        _loop = Task.Run(ProcessAsync);
        _ticker = Task.Run(TickAsync);
    }

    /// <summary>Feed a caption into the game. Non-blocking; safe from caption worker threads.</summary>
    public void Observe(CaptionEvent caption) => _inbox.Writer.TryWrite(new Input(caption, caption.At));

    /// <summary>Push a tick with an explicit clock, so tests drive time without sleeping.</summary>
    internal void InjectTick(DateTime now) => _inbox.Writer.TryWrite(new Input(null, now));

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TickPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                InjectTick(DateTime.Now);
            }
        }
        catch (OperationCanceledException) { /* CompleteAsync/Dispose stopping the ticker */ }
    }

    private async Task ProcessAsync()
    {
        await foreach (var input in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            IReadOnlyList<GameEvent> events;
            if (input.Caption is { } caption)
            {
                // The Me channel plays as the enrolled self name when there is one; far-side
                // captions play as their resolved name, else the channel/cluster label.
                var speaker = caption.Label == LiveCaptionEngine.MeLabel
                    ? _selfName ?? LiveCaptionEngine.MeLabel
                    : caption.SpeakerName;
                events = _rules.OnCaption(_state, speaker, caption.Caption, input.Now);
            }
            else
            {
                events = _rules.OnTick(_state, input.Now);
                if (events.Count == 0) continue; // idle tick: no repaint needed
            }

            foreach (var gameEvent in events)
            {
                ApplyAndNarrate(gameEvent, input.Now);
            }
            if (_state.BossDefeated && !_vanquishAnnounced)
            {
                _vanquishAnnounced = true;
                EventEmitted?.Invoke(input.Now, "green", "The boss is vanquished! The meeting's work is done");
            }
            StateChanged?.Invoke(BuildPanelState());
        }
    }

    private void ApplyAndNarrate(GameEvent gameEvent, DateTime now)
    {
        // Level derives from XP, so catch the crossing to narrate the level-up.
        var levelBefore = gameEvent.Effect == RpgEffect.Xp && gameEvent.Target != null
            ? _state.Find(gameEvent.Target)?.Level
            : null;

        _state.Apply(gameEvent);

        if (levelBefore is { } before && _state.Find(gameEvent.Target!) is { } ch && ch.Level > before)
        {
            EventEmitted?.Invoke(now, "cyan", $"{ch.Name} reaches level {ch.Level}!");
        }
        if (gameEvent.Narration is { } narration)
        {
            EventEmitted?.Invoke(now, NarrationColour(gameEvent.Effect), narration);
        }
    }

    private static string NarrationColour(RpgEffect effect) => effect switch
    {
        RpgEffect.BossDamage or RpgEffect.MobDefeat => "green",
        RpgEffect.BossHeal => "red",
        RpgEffect.MobSpawn => "magenta",
        RpgEffect.ClassAssign => "cyan",
        RpgEffect.CharacterHp or RpgEffect.CharacterMp => "yellow",
        _ => "white",
    };

    /// <summary>Map the game state to the display-owned presentation record. Card order is
    /// deliberately stable: self first, then everyone else by who spoke first. Ordering by
    /// recent activity made the cards jump around, which was jarring to glance at.</summary>
    private RpgPanelState BuildPanelState()
    {
        var self = _selfName ?? LiveCaptionEngine.MeLabel;
        var rows = _state.Party
            .Select((member, index) => (Member: member, Index: index))
            .OrderBy(x => string.Equals(x.Member.Name, self, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => new RpgPartyRow(
                ClassIcon(x.Member.Class),
                x.Member.Name,
                x.Member.Level,
                XpProgress(x.Member),
                x.Member.Hp, x.Member.MaxHp,
                x.Member.Mp, x.Member.MaxMp,
                RowColour(x.Member.Name, x.Index)))
            .ToList();
        var boss = new RpgBossRow(_state.Boss.Name, _state.Boss.Hp, _state.Boss.MaxHp, [.. _state.Mobs]);
        return new RpgPanelState(rows, boss);
    }

    /// <summary>Fraction of the way from the current level to the next. Level derives from XP
    /// as 1 + floor(sqrt(xp/100)), so level L spans [100(L-1)^2, 100L^2) XP.</summary>
    internal static double XpProgress(CharacterState member)
    {
        var level = member.Level;
        var floor = 100.0 * (level - 1) * (level - 1);
        var ceiling = 100.0 * level * level;
        return Math.Clamp((member.Xp - floor) / (ceiling - floor), 0, 1);
    }

    private string RowColour(string name, int joinIndex) =>
        name == (_selfName ?? LiveCaptionEngine.MeLabel)
            ? "cyan"
            : PartyPalette[joinIndex % PartyPalette.Length];

    // Deliberately single-cell glyphs, not emoji: double-width emoji misalign the bar columns
    // in some terminals.
    private static string ClassIcon(RpgClass cls) => cls switch
    {
        RpgClass.Fighter => "†",
        RpgClass.Mage => "✶",
        RpgClass.Cleric => "✚",
        RpgClass.Rogue => "◆",
        RpgClass.Bard => "♪",
        RpgClass.Ranger => "»",
        _ => "·",
    };

    /// <summary>Stop accepting input and drain the loop. Stops the ticker first, else the
    /// inbox never completes.</summary>
    public async Task CompleteAsync()
    {
        _cts.Cancel();
        await _ticker.ConfigureAwait(false); // never faults: the ticker swallows its cancellation
        _inbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _inbox.Writer.TryComplete();
        try { Task.WaitAll([_ticker, _loop], TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
    }
}
