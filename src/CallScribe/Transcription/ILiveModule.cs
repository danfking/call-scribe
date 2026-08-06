using Spectre.Console.Rendering;

namespace CallScribe.Transcription;

/// <summary>A pluggable dashboard component that watches the live caption stream and draws its own
/// panel into the single slot below the transcript (the boss fight, the coach, and future ones).
/// The display hosts whichever module is active and never sees a module's internals: it holds only
/// this interface and the Spectre renderable the module returns. Adding a component is implementing
/// this and registering it with the <see cref="LiveModuleHost"/>; no display code changes.
///
/// Threading mirrors the engines behind these modules: <see cref="Observe"/> is safe to call from
/// caption worker threads and returns immediately, while <see cref="RenderPanel"/> and
/// <see cref="ReserveRows"/> run on the display's render thread, so a module renders from a
/// thread-safe snapshot of its own state.</summary>
public interface ILiveModule : IDisposable
{
    /// <summary>Stable id for <c>/module &lt;id&gt;</c> and config, e.g. "rpg" or "coach".</summary>
    string Id { get; }

    /// <summary>Human label for the panel header and the module switcher.</summary>
    string Title { get; }

    /// <summary>Feed a caption in. Non-blocking: the module's own loop does the work.</summary>
    void Observe(CaptionEvent caption);

    /// <summary>Show or hide this module. A hidden module pauses its own work (the MVP rule): a
    /// model-backed module makes no calls, a timer-driven one stops ticking. Any always-on side
    /// effect a module also owns (e.g. transcript persistence) is deliberately not gated by this.</summary>
    void SetActive(bool active);

    /// <summary>How many rows the module wants in the slot given the max available (mirrors the old
    /// RpgReservedRows / CoachPanelRows). Called on the render thread.</summary>
    int ReserveRows(int maxRows);

    /// <summary>Render the slot content. Called on the render thread; render from a snapshot.</summary>
    IRenderable RenderPanel();

    /// <summary>Drain any in-flight work at the end of the meeting.</summary>
    Task CompleteAsync();

    /// <summary>Raised when the module's state changed and the slot should repaint.</summary>
    event Action? Changed;

    /// <summary>Raised with a ready-to-print Spectre markup line for the non-interactive
    /// (redirected-output) fallback, where there is no live panel; the display prints it then.</summary>
    event Action<string>? Narrated;
}
