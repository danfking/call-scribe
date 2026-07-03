using CallScribe.Rpg;
using CallScribe.Transcription;

namespace CallScribe.Tests;

public class RpgEngineTests
{
    private static readonly DateTime T0 = new(2026, 7, 3, 10, 0, 0);

    [Fact]
    public async Task Captions_BuildTheParty_AndTheSnapshotTracksTheBoss()
    {
        using var engine = new RpgEngine(new RpgRules(), selfName: "Dan");
        var snapshots = new List<RpgPanelState>();
        engine.StateChanged += snapshots.Add;

        engine.Observe(new CaptionEvent(T0, LiveCaptionEngine.MeLabel, "Let's fix the build first."));
        engine.Observe(new CaptionEvent(T0.AddSeconds(2), LiveCaptionEngine.OthersLabel, "Can you share the log?", "Priya"));
        await engine.CompleteAsync();

        Assert.NotEmpty(snapshots);
        var last = snapshots[^1];
        Assert.Equal(["Dan", "Priya"], last.Party.Select(p => p.Name).OrderBy(n => n));
        // A decision (6) plus a question (2) landed on the boss.
        Assert.Equal(RpgRules.Tuning.BossBaseHp
                     - RpgRules.Tuning.DecisionBossDamage
                     - RpgRules.Tuning.QuestionBossDamage, last.Boss.Hp);
    }

    [Fact]
    public async Task MeChannel_PlaysAsTheChannelLabel_WithoutASelfName()
    {
        using var engine = new RpgEngine(new RpgRules());
        var snapshots = new List<RpgPanelState>();
        engine.StateChanged += snapshots.Add;

        engine.Observe(new CaptionEvent(T0, LiveCaptionEngine.MeLabel, "Morning all."));
        await engine.CompleteAsync();

        var row = Assert.Single(snapshots[^1].Party);
        Assert.Equal(LiveCaptionEngine.MeLabel, row.Name);
        Assert.Equal("cyan", row.Colour);
    }

    [Fact]
    public async Task NarratedEvents_ReachTheLog()
    {
        using var engine = new RpgEngine(new RpgRules());
        var lines = new List<string>();
        engine.EventEmitted += (_, _, text) => lines.Add(text);

        engine.Observe(new CaptionEvent(T0, LiveCaptionEngine.OthersLabel,
            "Agreed, action item for Priya.", "Gavin"));
        await engine.CompleteAsync();

        Assert.Contains(lines, l => l.Contains("Decisive blow by Gavin"));
    }

    [Fact]
    public async Task SilenceTick_NarratesAndDrainsTheParty()
    {
        using var engine = new RpgEngine(new RpgRules());
        var lines = new List<string>();
        var snapshots = new List<RpgPanelState>();
        engine.EventEmitted += (_, _, text) => lines.Add(text);
        engine.StateChanged += snapshots.Add;

        engine.Observe(new CaptionEvent(T0, LiveCaptionEngine.OthersLabel, "Give me a minute.", "Gavin"));
        engine.InjectTick(T0.AddSeconds(50));
        await engine.CompleteAsync();

        Assert.Contains(lines, l => l.Contains("boss regenerates"));
        var row = Assert.Single(snapshots[^1].Party);
        // +2 for speaking (capped at full), then -1 for the silence.
        Assert.Equal(row.MaxHp - RpgRules.Tuning.SilenceHpDrain, row.Hp);
    }

    [Fact]
    public async Task IdleTicks_DoNotRepaint()
    {
        using var engine = new RpgEngine(new RpgRules());
        var snapshots = new List<RpgPanelState>();
        engine.StateChanged += snapshots.Add;

        engine.InjectTick(T0); // empty party: nothing to do
        await engine.CompleteAsync();

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task BossDefeat_IsAnnouncedExactlyOnce()
    {
        using var engine = new RpgEngine(new RpgRules());
        var lines = new List<string>();
        engine.EventEmitted += (_, _, text) => lines.Add(text);

        // Enough decisive blows to take the boss from 100 to 0 and keep swinging past it.
        for (var i = 0; i < 20; i++)
        {
            engine.Observe(new CaptionEvent(T0.AddSeconds(i * 3), LiveCaptionEngine.OthersLabel,
                $"Agreed, we will also do item {i}.", "Gavin"));
        }
        await engine.CompleteAsync();

        Assert.Single(lines, l => l.Contains("vanquished"));
    }

    [Fact]
    public async Task LevelUp_IsNarrated_WhenXpCrossesTheCurve()
    {
        using var engine = new RpgEngine(new RpgRules());
        var lines = new List<string>();
        engine.EventEmitted += (_, _, text) => lines.Add(text);

        // Level 2 sits at 100 XP; ten long captions at the 10 XP cap cross it.
        var longLine = string.Join(" ", Enumerable.Repeat("word", 120));
        for (var i = 0; i < 10; i++)
        {
            engine.Observe(new CaptionEvent(T0.AddSeconds(i * 3), LiveCaptionEngine.OthersLabel, longLine, "Gavin"));
        }
        await engine.CompleteAsync();

        Assert.Contains(lines, l => l.Contains("Gavin reaches level 2"));
    }
}
