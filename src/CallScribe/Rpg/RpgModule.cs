using CallScribe.Transcription;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CallScribe.Rpg;

/// <summary>The boss-fight dashboard component: composes an <see cref="RpgEngine"/> (the game) with
/// an <see cref="RpgPanel"/> (the rendering). The display hosts <see cref="RenderPanel"/> and knows
/// nothing about the game. Feeding captions is gated on <see cref="SetActive"/>: a hidden boss fight
/// pauses (the MVP rule), so captions it did not see leave a gap when it is shown again.</summary>
public sealed class RpgModule : ILiveModule
{
    private readonly RpgEngine _engine;
    private readonly RpgPanel _panel = new();
    private volatile bool _active;

    public RpgModule(string? selfName)
    {
        _engine = new RpgEngine(new RpgRules(), selfName: selfName);
        _engine.StateChanged += OnState;
        _engine.EventEmitted += OnEvent;
    }

    public string Id => "rpg";
    public string Title => "Boss fight";

    public event Action? Changed;
    public event Action<string>? Narrated;

    public void Observe(CaptionEvent caption)
    {
        if (_active) _engine.Observe(caption);
    }

    public void SetActive(bool active) => _active = active;

    public int ReserveRows(int maxRows) => _panel.ReserveRows();

    public IRenderable RenderPanel() => _panel.Render();

    public Task CompleteAsync() => _engine.CompleteAsync();

    public void Dispose() => _engine.Dispose();

    private void OnState(RpgPanelState state)
    {
        _panel.Update(state);
        Changed?.Invoke();
    }

    private void OnEvent(DateTime at, string colour, string text)
    {
        // Non-interactive (redirected) fallback: a plain timestamped line. The event is still
        // recorded in the panel so both paths stay consistent.
        Narrated?.Invoke($"[grey]{at:HH:mm:ss}[/] [{colour}]rpg ▸[/] {text.EscapeMarkup()}");
        _panel.AddEvent(at, colour, text);
        Changed?.Invoke();
    }
}
