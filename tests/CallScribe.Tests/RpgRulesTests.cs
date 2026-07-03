using CallScribe.Rpg;

namespace CallScribe.Tests;

public class RpgRulesTests
{
    private static readonly DateTime T0 = new(2026, 7, 3, 10, 0, 0);

    /// <summary>Run one caption through the rules and apply the events, the way the engine does.</summary>
    private static List<GameEvent> Caption(RpgRules rules, GameState state, string speaker, string text, DateTime at)
    {
        var events = rules.OnCaption(state, speaker, text, at).ToList();
        foreach (var e in events) state.Apply(e);
        return events;
    }

    private static List<GameEvent> Tick(RpgRules rules, GameState state, DateTime at)
    {
        var events = rules.OnTick(state, at).ToList();
        foreach (var e in events) state.Apply(e);
        return events;
    }

    [Fact]
    public void Speaking_GivesHpAndXp()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");

        var events = Caption(rules, state, "Gavin", "Morning all, quick update from my side.", T0);

        Assert.Contains(events, e => e.Effect == RpgEffect.CharacterHp && e.Amount == RpgRules.Tuning.SpeakHpGain);
        Assert.Contains(events, e => e.Effect == RpgEffect.Xp && e.Amount >= 1);
        Assert.Single(state.Party);
    }

    [Fact]
    public void Xp_IsCappedPerCaption()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");
        var monologue = string.Join(" ", Enumerable.Repeat("word", 500));

        var events = Caption(rules, state, "Gavin", monologue, T0);

        var xp = Assert.Single(events, e => e.Effect == RpgEffect.Xp);
        Assert.Equal(RpgRules.Tuning.XpCapPerCaption, xp.Amount);
    }

    [Fact]
    public void Question_CostsManaAndDamagesTheBoss()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");

        var events = Caption(rules, state, "Priya", "Where did the migration script land?", T0);

        Assert.Contains(events, e => e.Effect == RpgEffect.CharacterMp && e.Amount == -RpgRules.Tuning.QuestionMpCost);
        Assert.Contains(events, e => e.Effect == RpgEffect.BossDamage && e.Amount == RpgRules.Tuning.QuestionBossDamage);
        Assert.Equal(RpgRules.Tuning.BossBaseHp - RpgRules.Tuning.QuestionBossDamage, state.Boss.Hp);
    }

    [Fact]
    public void Question_WithoutMana_DoesNotCast()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");
        state.GetOrAdd("Priya", T0).Mp = 0;

        var events = Caption(rules, state, "Priya", "Can we revisit the rollback plan?", T0);

        Assert.DoesNotContain(events, e => e.Effect == RpgEffect.CharacterMp);
        Assert.DoesNotContain(events, e => e.Effect == RpgEffect.BossDamage);
    }

    [Theory]
    [InlineData("Where did the script land?")]
    [InlineData("why is the pipeline stuck")]
    [InlineData("Should we ship on Thursday")]
    public void Question_Detection_CoversMarksAndOpeners(string text) => Assert.True(RpgRules.IsQuestion(text));

    [Theory]
    [InlineData("The pipeline finished overnight.")]
    [InlineData("That lands on Thursday.")]
    public void Question_Detection_IgnoresStatements(string text) => Assert.False(RpgRules.IsQuestion(text));

    [Fact]
    public void Decision_HitsTheBossHard()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");

        var events = Caption(rules, state, "Gavin", "I'll chase the platform team after this call.", T0);

        Assert.Contains(events, e => e.Effect == RpgEffect.BossDamage && e.Amount == RpgRules.Tuning.DecisionBossDamage);
    }

    [Fact]
    public void Combo_FiresOnceWhenThreeVoicesOverlap()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");

        Caption(rules, state, "Gavin", "The staging run is queued.", T0);
        Caption(rules, state, "Priya", "The config change is merged.", T0.AddSeconds(3));
        var third = Caption(rules, state, "Dan", "Then the pipeline is unblocked.", T0.AddSeconds(6));
        var fourth = Caption(rules, state, "Gavin", "It is indeed unblocked now.", T0.AddSeconds(9));

        Assert.Contains(third, e => e.Effect == RpgEffect.BossDamage && e.Amount == RpgRules.Tuning.ComboBossDamage);
        Assert.DoesNotContain(fourth, e => e.Effect == RpgEffect.BossDamage && e.Amount == RpgRules.Tuning.ComboBossDamage);
    }

    [Fact]
    public void CircularDiscussion_HealsTheBoss()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");
        const string repeated = "the quarterly report needs the updated revenue numbers from finance";

        Caption(rules, state, "Gavin", repeated, T0);
        Caption(rules, state, "Priya", "Sure, noted.", T0.AddSeconds(2));
        Caption(rules, state, "Dan", "Moving to the deploy topic now.", T0.AddSeconds(4));
        Caption(rules, state, "Priya", "Deploy is scheduled for Thursday.", T0.AddSeconds(6));
        Caption(rules, state, "Gavin", "Thursday works for support cover.", T0.AddSeconds(8));
        Caption(rules, state, "Dan", "Support cover is confirmed already.", T0.AddSeconds(10));
        var looped = Caption(rules, state, "Priya", repeated, T0.AddSeconds(12));

        Assert.Contains(looped, e => e.Effect == RpgEffect.BossHeal && e.Amount == RpgRules.Tuning.CircularBossHeal);
    }

    [Fact]
    public void Monologue_WindsTheSpeaker_OncePerStreak()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");

        var all = new List<GameEvent>();
        for (var i = 0; i < 7; i++)
        {
            all.AddRange(Caption(rules, state, "Gavin", $"And another distinct point number {i}.", T0.AddSeconds(i * 3)));
        }

        var winded = all.Where(e => e.Effect == RpgEffect.CharacterMp && e.Amount == -RpgRules.Tuning.MonologueMpCost);
        Assert.Single(winded);
    }

    [Fact]
    public void Silence_HealsTheBossAndDrainsTheParty_OncePerWindow()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");
        Caption(rules, state, "Gavin", "Give me a minute to check the pipeline.", T0);
        var hpAfterSpeaking = state.Party[0].Hp;

        var firstTick = Tick(rules, state, T0.AddSeconds(50));
        var secondTick = Tick(rules, state, T0.AddSeconds(55));

        Assert.Contains(firstTick, e => e.Effect == RpgEffect.BossHeal && e.Amount == RpgRules.Tuning.SilenceBossHeal);
        Assert.Contains(firstTick, e => e.Effect == RpgEffect.CharacterHp && e.Amount == -RpgRules.Tuning.SilenceHpDrain);
        Assert.DoesNotContain(secondTick, e => e.Effect == RpgEffect.BossHeal);
        Assert.Equal(hpAfterSpeaking - RpgRules.Tuning.SilenceHpDrain, state.Party[0].Hp);
    }

    [Fact]
    public void Mana_RegeneratesAfterIdle()
    {
        var rules = new RpgRules();
        var state = new GameState("The Meeting");
        Caption(rules, state, "Priya", "Where did the script land?", T0); // casts: MP down, LastCastAt = T0

        var early = Tick(rules, state, T0.AddSeconds(10));
        var late = Tick(rules, state, T0.AddSeconds(20));

        Assert.DoesNotContain(early, e => e.Effect == RpgEffect.CharacterMp);
        Assert.Contains(late, e => e.Effect == RpgEffect.CharacterMp && e.Amount == RpgRules.Tuning.MpRegen);
    }

    [Fact]
    public void Apply_ClampsHpAndMp_AndKeepsAVanquishedBossDown()
    {
        var state = new GameState("The Meeting");
        var ch = state.GetOrAdd("Gavin", T0);

        state.Apply(new GameEvent(RpgEffect.CharacterHp, "Gavin", 999));
        Assert.Equal(ch.MaxHp, ch.Hp);
        state.Apply(new GameEvent(RpgEffect.CharacterMp, "Gavin", -999));
        Assert.Equal(0, ch.Mp);

        state.Apply(new GameEvent(RpgEffect.BossDamage, null, 999));
        Assert.True(state.BossDefeated);
        state.Apply(new GameEvent(RpgEffect.BossHeal, null, 50));
        Assert.Equal(0, state.Boss.Hp);
    }

    [Fact]
    public void Boss_ScalesWithThePartyBeyondTwo()
    {
        var state = new GameState("The Meeting");
        state.GetOrAdd("Gavin", T0);
        state.GetOrAdd("Priya", T0);
        Assert.Equal(RpgRules.Tuning.BossBaseHp, state.Boss.MaxHp);

        state.GetOrAdd("Dan", T0);
        Assert.Equal(RpgRules.Tuning.BossBaseHp + RpgRules.Tuning.BossHpPerExtraMember, state.Boss.MaxHp);
    }
}
