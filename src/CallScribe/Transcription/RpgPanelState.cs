namespace CallScribe.Transcription;

/// <summary>Presentation-only snapshot of the RPG panel (party rows, boss row). Owned by the
/// display layer so <see cref="LiveStatusDisplay"/> never references the Rpg namespace, the same
/// convention as the coach's primitive colour/glyph hints: the Rpg module maps its game state into
/// this shape and the display just renders it.</summary>
public sealed record RpgPanelState(
    IReadOnlyList<RpgPartyRow> Party,
    RpgBossRow Boss);

/// <summary>One party member's line: class icon and colour are presentation hints chosen by the
/// game module; the display renders the bars from the raw numbers.</summary>
public sealed record RpgPartyRow(
    string ClassIcon,
    string Name,
    int Level,
    int Hp,
    int MaxHp,
    int Mp,
    int MaxMp,
    string Colour);

/// <summary>The boss line: the meeting's objective/blocker with its own HP bar, plus any live
/// trash mobs (tangents) by name.</summary>
public sealed record RpgBossRow(
    string Name,
    int Hp,
    int MaxHp,
    IReadOnlyList<string> MobNames);
