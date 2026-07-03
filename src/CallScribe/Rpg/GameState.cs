namespace CallScribe.Rpg;

/// <summary>A party member's class. Assigned by the game master (later phase) or a deterministic
/// fallback; Unassigned members fight just fine, they simply have no icon flair yet.</summary>
public enum RpgClass { Unassigned, Fighter, Mage, Cleric, Rogue, Bard, Ranger }

/// <summary>One meeting participant as a party member. Mutable, touched only on the engine's
/// loop task (like the coach's context window), so it needs no locking.</summary>
public sealed class CharacterState
{
    public CharacterState(string name)
    {
        Name = name;
        Hp = MaxHp;
        Mp = MaxMp;
    }

    public string Name { get; }
    public RpgClass Class { get; set; } = RpgClass.Unassigned;

    /// <summary>XP earned so far. Level (and with it max HP/MP) derives from XP, never stored,
    /// so a mid-meeting level-up shows immediately and curve retunes apply retroactively.</summary>
    public int Xp { get; set; }

    public int Level => 1 + (int)Math.Sqrt(Xp / 100.0);
    public int MaxHp => RpgRules.Tuning.BaseHp + RpgRules.Tuning.HpPerLevel * (Level - 1);
    public int MaxMp => RpgRules.Tuning.BaseMp + RpgRules.Tuning.MpPerLevel * (Level - 1);

    public int Hp { get; set; }
    public int Mp { get; set; }

    // Rules bookkeeping (also feeds the later deterministic class fallback).
    public DateTime LastSpokeAt { get; set; }
    public DateTime LastCastAt { get; set; }
    public int WordCount { get; set; }
    public int QuestionCount { get; set; }
    public int DecisionCount { get; set; }
}

/// <summary>The meeting itself as the fight's boss: its HP counts down to "meeting won".</summary>
public sealed class BossState(string name)
{
    public string Name { get; } = name;
    public int MaxHp { get; set; } = RpgRules.Tuning.BossBaseHp;
    public int Hp { get; set; } = RpgRules.Tuning.BossBaseHp;
}

/// <summary>The whole game: party, boss, live mobs, and the rules' rolling bookkeeping. Mutable
/// and single-threaded by construction: only the engine's loop task touches it.</summary>
public sealed class GameState(string bossName)
{
    private readonly Dictionary<string, CharacterState> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CharacterState> _party = [];

    public IReadOnlyList<CharacterState> Party => _party;
    public BossState Boss { get; } = new(bossName);

    /// <summary>Live trash-mob names (tangents). Spawned/defeated by game events.</summary>
    public List<string> Mobs { get; } = [];

    public bool BossDefeated => Boss.Hp <= 0;

    // Rolling bookkeeping for the rules: recent caption word-sets (for circular-discussion
    // detection), plus the once-per-window guards.
    internal readonly List<(DateTime At, string Speaker, HashSet<string> Tokens)> RecentCaptions = [];
    internal DateTime LastCaptionAt;
    internal DateTime LastComboAt;
    internal DateTime LastCircularAt;
    internal DateTime LastSilenceAt;
    internal string? StreakSpeaker;
    internal int StreakLength;
    internal bool StreakPenalised;

    public CharacterState? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The party member with this name, created on first sight. The boss scales up for
    /// every member beyond two, so a crowded meeting is a longer fight, not a trivial one.</summary>
    public CharacterState GetOrAdd(string name, DateTime now)
    {
        if (_byName.TryGetValue(name, out var existing)) return existing;

        var character = new CharacterState(name) { LastSpokeAt = now, LastCastAt = now };
        _byName[name] = character;
        _party.Add(character);
        if (_party.Count > 2)
        {
            Boss.MaxHp += RpgRules.Tuning.BossHpPerExtraMember;
            Boss.Hp += RpgRules.Tuning.BossHpPerExtraMember;
        }
        return character;
    }

    /// <summary>Apply one game event. The single place stats move, shared by the deterministic
    /// rules and (later) the game master, so unknown targets are simply ignored rather than
    /// trusted.</summary>
    public void Apply(GameEvent e)
    {
        switch (e.Effect)
        {
            case RpgEffect.BossDamage:
                Boss.Hp = Math.Max(0, Boss.Hp - e.Amount);
                break;
            case RpgEffect.BossHeal:
                // A vanquished boss stays down; late circular chatter can't resurrect it.
                if (Boss.Hp > 0) Boss.Hp = Math.Min(Boss.MaxHp, Boss.Hp + e.Amount);
                break;
            case RpgEffect.CharacterHp when e.Target != null && Find(e.Target) is { } ch:
                ch.Hp = Math.Clamp(ch.Hp + e.Amount, 0, ch.MaxHp);
                break;
            case RpgEffect.CharacterMp when e.Target != null && Find(e.Target) is { } cm:
                cm.Mp = Math.Clamp(cm.Mp + e.Amount, 0, cm.MaxMp);
                break;
            case RpgEffect.Xp when e.Target != null && Find(e.Target) is { } cx:
                cx.Xp += e.Amount;
                break;
            case RpgEffect.MobSpawn when e.Target != null:
                if (!Mobs.Contains(e.Target, StringComparer.OrdinalIgnoreCase)) Mobs.Add(e.Target);
                break;
            case RpgEffect.MobDefeat when e.Target != null:
                Mobs.RemoveAll(m => string.Equals(m, e.Target, StringComparison.OrdinalIgnoreCase));
                break;
            case RpgEffect.ClassAssign when e.Target != null && e.Class is { } cls && Find(e.Target) is { } cc:
                if (cc.Class == RpgClass.Unassigned) cc.Class = cls;
                break;
        }
    }
}
