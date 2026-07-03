namespace CallScribe.Rpg;

/// <summary>What a game event does to the state. Both the deterministic rules and (in a later
/// phase) the LLM game master emit these; <see cref="GameState.Apply"/> is the single place they
/// take effect, so the two sources cannot drift in how stats move.</summary>
public enum RpgEffect
{
    /// <summary>Damage the boss (Amount is the damage).</summary>
    BossDamage,

    /// <summary>Heal the boss (Amount is the heal). A vanquished boss stays down.</summary>
    BossHeal,

    /// <summary>Spawn a trash mob named Target (a tangent).</summary>
    MobSpawn,

    /// <summary>Defeat the mob named Target (back on topic).</summary>
    MobDefeat,

    /// <summary>Change a party member's HP by Amount (signed delta, clamped to 0..max).</summary>
    CharacterHp,

    /// <summary>Change a party member's MP by Amount (signed delta, clamped to 0..max).</summary>
    CharacterMp,

    /// <summary>Award Target Amount XP.</summary>
    Xp,

    /// <summary>Assign Class to Target, only while their class is still Unassigned.</summary>
    ClassAssign,

    /// <summary>No state change; the Narration is the whole point.</summary>
    Narrate,
}

/// <summary>One thing that happened in the game. Target is a party member or mob name (null for
/// boss effects); Narration, when present, is shown in the panel's event log.</summary>
public sealed record GameEvent(
    RpgEffect Effect,
    string? Target,
    int Amount,
    string? Narration = null,
    RpgClass? Class = null);
