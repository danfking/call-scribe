using CallScribe.Transcription;

namespace CallScribe.Tests;

/// <summary>Renders the RPG area to plain text via the display's test seam and asserts the
/// two-column layout: battle log on the left, stacked character/boss cards on the right.</summary>
public class RpgPanelLayoutTests
{
    private static readonly DateTime T0 = new(2026, 7, 3, 10, 0, 0);

    private static LiveStatusDisplay DisplayWithSnapshot()
    {
        var display = new LiveStatusDisplay();
        display.EnableRpgPanel();
        display.UpdateRpg(new RpgPanelState(
            [
                new RpgPartyRow("·", "Dan", 1, 24, 30, 2, 10, "cyan"),
                new RpgPartyRow("·", "Priya", 1, 30, 30, 10, 10, "yellow"),
            ],
            new RpgBossRow("The Meeting", 95, 125, [])));
        display.PrintRpgEvent(T0, "green", "Decisive blow by Dan! The boss staggers (6 dmg)");
        return display;
    }

    [Fact]
    public void LogAndCards_RenderSideBySide()
    {
        using var display = DisplayWithSnapshot();
        var lines = display.RenderRpgToText(100).Split('\n');

        // The log pane's header and the first character card's header share the top row:
        // that is what makes it a two-column layout rather than stacked panels.
        var top = Array.Find(lines, l => l.Contains("boss fight"));
        Assert.NotNull(top);
        Assert.Contains("Dan L1", top);
    }

    [Fact]
    public void EveryPartyMemberAndTheBoss_GetTheirOwnCard()
    {
        using var display = DisplayWithSnapshot();
        var text = display.RenderRpgToText(100);

        Assert.Contains("Dan L1", text);
        Assert.Contains("Priya L1", text);
        Assert.Contains("BOSS The Meeting", text);
        Assert.Contains("95/125", text);
    }

    [Fact]
    public void Readings_OverlayTheBars()
    {
        using var display = DisplayWithSnapshot();
        var text = display.RenderRpgToText(100);

        // The fill is pure background colour (invisible in this colourless render); what the
        // plain text pins is the bar's end caps and the reading centred between them, at the
        // 9-cell width the test host's fallback window yields.
        Assert.Contains("|  24/30  |", text);
        Assert.Contains("|  2/10   |", text);
    }

    [Fact]
    public void BattleLog_ShowsEvents_WithoutTimestamps()
    {
        using var display = DisplayWithSnapshot();
        var text = display.RenderRpgToText(100);

        Assert.Contains("> Decisive blow by Dan!", text);
        Assert.DoesNotContain("10:00:00", text);
    }

    [Fact]
    public void BeforeTheFirstSnapshot_APlaceholderPanelShows()
    {
        using var display = new LiveStatusDisplay();
        display.EnableRpgPanel();
        var text = display.RenderRpgToText(100);

        Assert.Contains("the party assembles", text);
    }
}
