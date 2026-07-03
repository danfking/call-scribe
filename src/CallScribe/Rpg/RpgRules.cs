using CallScribe.Transcription;

namespace CallScribe.Rpg;

/// <summary>The deterministic game rules: pure functions of the caption stream (plus a wall-clock
/// tick) that move stats instantly, no model needed. All numbers live in <see cref="Tuning"/> so
/// the game's feel is retuned in one place. The clock is always passed in, so tests never sleep.</summary>
public sealed class RpgRules
{
    /// <summary>Every gameplay number in one place. The values are a first cut chosen for feel,
    /// validated by replaying scripted meetings, not derivation; expect them to move.</summary>
    internal static class Tuning
    {
        // Character baselines. Level derives from XP: level = 1 + sqrt(xp / 100).
        public const int BaseHp = 30;
        public const int HpPerLevel = 5;
        public const int BaseMp = 10;
        public const int MpPerLevel = 2;

        // The boss scales with the crowd so a big meeting is a longer fight.
        public const int BossBaseHp = 100;
        public const int BossHpPerExtraMember = 25;

        // Speaking keeps you in the fight: a little HP and XP per utterance.
        public const int SpeakHpGain = 2;
        public const int WordsPerXp = 10;
        public const int XpCapPerCaption = 10;

        // A question is a cast: costs mana, chips the boss. No mana, no cast.
        public const int QuestionMpCost = 4;
        public const int QuestionBossDamage = 2;

        // A decision or commitment is the real damage dealer.
        public const int DecisionBossDamage = 6;

        // Lively back-and-forth (3+ voices inside the window) lands a combo bonus.
        public static readonly TimeSpan ComboWindow = TimeSpan.FromSeconds(30);
        public const int ComboSpeakers = 3;
        public const int ComboBossDamage = 3;

        // Circular discussion heals the boss: the new utterance heavily overlaps something
        // said at least MinGap captions ago (token overlap, same helper as the echo filter).
        public const double CircularOverlapThreshold = 0.6;
        public const int CircularMinTokens = 4;
        public const int CircularMinGapCaptions = 5;
        public const int CaptionRing = 40;
        public const int CircularBossHeal = 4;
        public static readonly TimeSpan CircularWindow = TimeSpan.FromSeconds(30);

        // A long monologue winds the speaker (once per streak).
        public const int MonologueStreak = 5;
        public const int MonologueMpCost = 3;

        // Dead air heals the boss and drains the party (once per silence window).
        public static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(45);
        public const int SilenceBossHeal = 2;
        public const int SilenceHpDrain = 1;

        // Mana trickles back while a character hasn't cast for a while (per tick).
        public static readonly TimeSpan MpRegenAfter = TimeSpan.FromSeconds(15);
        public const int MpRegen = 1;
    }

    private static readonly string[] InterrogativeOpeners =
        ["who", "what", "when", "where", "why", "how", "should", "could", "can", "is", "are", "do", "does"];

    private static readonly string[] DecisionPhrases =
        ["let's ", "we'll ", "i'll ", "i will ", "we will ", "agreed", "action item", "decision", "next step"];

    /// <summary>Score one utterance. Mutates only the rules' bookkeeping (caption ring, streaks,
    /// per-character counters); every stat change comes back as a <see cref="GameEvent"/> for the
    /// engine to apply, so the rules and the game master move stats through the same door.</summary>
    public IReadOnlyList<GameEvent> OnCaption(GameState state, string speaker, string text, DateTime now)
    {
        var events = new List<GameEvent>();
        var character = state.GetOrAdd(speaker, now);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        // Speaking keeps you present: a little HP back and some XP for the contribution.
        events.Add(new GameEvent(RpgEffect.CharacterHp, speaker, Tuning.SpeakHpGain));
        events.Add(new GameEvent(RpgEffect.Xp, speaker,
            Math.Min(Tuning.XpCapPerCaption, Math.Max(1, words / Tuning.WordsPerXp))));

        if (IsQuestion(text))
        {
            character.QuestionCount++;
            if (character.Mp > 0)
            {
                character.LastCastAt = now;
                events.Add(new GameEvent(RpgEffect.CharacterMp, speaker, -Tuning.QuestionMpCost));
                events.Add(new GameEvent(RpgEffect.BossDamage, null, Tuning.QuestionBossDamage,
                    $"{speaker}'s question probes the boss ({Tuning.QuestionBossDamage} dmg)"));
            }
        }

        if (IsDecision(text))
        {
            character.DecisionCount++;
            events.Add(new GameEvent(RpgEffect.BossDamage, null, Tuning.DecisionBossDamage,
                $"Decisive blow by {speaker}! The boss staggers ({Tuning.DecisionBossDamage} dmg)"));
        }

        var tokens = TokenOverlap.Tokenize(text);
        state.RecentCaptions.Add((now, speaker, tokens));
        if (state.RecentCaptions.Count > Tuning.CaptionRing)
        {
            state.RecentCaptions.RemoveRange(0, state.RecentCaptions.Count - Tuning.CaptionRing);
        }

        // Combo: three or more distinct voices inside the window, at most once per window.
        if (now - state.LastComboAt >= Tuning.ComboWindow)
        {
            var voices = state.RecentCaptions
                .Where(c => now - c.At <= Tuning.ComboWindow)
                .Select(c => c.Speaker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (voices >= Tuning.ComboSpeakers)
            {
                state.LastComboAt = now;
                events.Add(new GameEvent(RpgEffect.BossDamage, null, Tuning.ComboBossDamage,
                    $"Party combo! {voices} voices strike as one ({Tuning.ComboBossDamage} dmg)"));
            }
        }

        // Circular discussion: this utterance heavily overlaps something said a while back.
        if (now - state.LastCircularAt >= Tuning.CircularWindow)
        {
            var stale = state.RecentCaptions.Count - 1 - Tuning.CircularMinGapCaptions;
            for (var i = 0; i < stale; i++)
            {
                if (TokenOverlap.OverlapCoefficient(tokens, state.RecentCaptions[i].Tokens,
                        Tuning.CircularMinTokens) >= Tuning.CircularOverlapThreshold)
                {
                    state.LastCircularAt = now;
                    events.Add(new GameEvent(RpgEffect.BossHeal, null, Tuning.CircularBossHeal,
                        $"The discussion circles… the boss feeds (+{Tuning.CircularBossHeal} HP)"));
                    break;
                }
            }
        }

        // Monologue: the same voice for a while winds the speaker, once per streak.
        if (string.Equals(state.StreakSpeaker, speaker, StringComparison.OrdinalIgnoreCase))
        {
            state.StreakLength++;
        }
        else
        {
            state.StreakSpeaker = speaker;
            state.StreakLength = 1;
            state.StreakPenalised = false;
        }
        if (state.StreakLength >= Tuning.MonologueStreak && !state.StreakPenalised)
        {
            state.StreakPenalised = true;
            events.Add(new GameEvent(RpgEffect.CharacterMp, speaker, -Tuning.MonologueMpCost,
                $"{speaker} is winded from the long monologue"));
        }

        character.LastSpokeAt = now;
        character.WordCount += words;
        state.LastCaptionAt = now;
        return events;
    }

    /// <summary>Score a wall-clock tick: silence feeds the boss, and idle casters regain mana.</summary>
    public IReadOnlyList<GameEvent> OnTick(GameState state, DateTime now)
    {
        var events = new List<GameEvent>();
        if (state.Party.Count == 0) return events;

        if (now - state.LastCaptionAt >= Tuning.SilenceWindow && now - state.LastSilenceAt >= Tuning.SilenceWindow)
        {
            state.LastSilenceAt = now;
            events.Add(new GameEvent(RpgEffect.BossHeal, null, Tuning.SilenceBossHeal,
                $"Silence… the boss regenerates (+{Tuning.SilenceBossHeal} HP)"));
            foreach (var member in state.Party)
            {
                events.Add(new GameEvent(RpgEffect.CharacterHp, member.Name, -Tuning.SilenceHpDrain));
            }
        }

        foreach (var member in state.Party)
        {
            if (member.Mp < member.MaxMp && now - member.LastCastAt >= Tuning.MpRegenAfter)
            {
                events.Add(new GameEvent(RpgEffect.CharacterMp, member.Name, Tuning.MpRegen));
            }
        }
        return events;
    }

    /// <summary>A question: ends with a question mark, or opens with an interrogative. The
    /// live model punctuates reliably, so the mark carries most of the weight; the opener list
    /// catches the trailed-off ones.</summary>
    internal static bool IsQuestion(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith('?')) return true;
        var firstWord = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return firstWord.Length > 0
               && InterrogativeOpeners.Contains(firstWord[0].TrimEnd(','), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A decision or commitment, by phrase list. Crude but cheap; the game master
    /// (later phase) adds the judgement calls.</summary>
    internal static bool IsDecision(string text) =>
        DecisionPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}
