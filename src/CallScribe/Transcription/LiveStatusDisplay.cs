using Spectre.Console;

namespace CallScribe.Transcription;

public enum TrackState { Listening, Hearing, Transcribing }

/// <summary>Owns the listen-mode console: caption lines plus a single status line
/// at the bottom showing what each track is doing (listening / hearing audio /
/// transcribing). The status line is rewritten in place and disabled entirely
/// when output is redirected.</summary>
public sealed class LiveStatusDisplay
{
    private readonly Lock _lock = new();
    private readonly List<(string Label, string Colour)> _order = [];
    private readonly Dictionary<string, TrackState> _states = [];
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private bool _statusVisible;

    public void Register(string label, string colour)
    {
        lock (_lock)
        {
            _order.Add((label, colour));
            _states[label] = TrackState.Listening;
            Redraw();
        }
    }

    public void SetState(string label, TrackState state)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(label, out var current) && current == state) return;
            _states[label] = state;
            Redraw();
        }
    }

    public void PrintCaption(DateTime at, string colour, string label, string caption)
    {
        lock (_lock)
        {
            ClearStatus();
            AnsiConsole.MarkupLine(
                $"[grey]{at:HH:mm:ss}[/] [{colour}]{label,-6}[/] {caption.EscapeMarkup()}");
            DrawStatus();
        }
    }

    /// <summary>Remove the status line so the shell prompt lands cleanly.</summary>
    public void Shutdown()
    {
        lock (_lock) ClearStatus();
    }

    private void Redraw()
    {
        ClearStatus();
        DrawStatus();
    }

    private void ClearStatus()
    {
        if (!_interactive || !_statusVisible) return;
        var width = Math.Max(Console.WindowWidth - 1, 1);
        Console.Write('\r' + new string(' ', width) + '\r');
        _statusVisible = false;
    }

    private void DrawStatus()
    {
        if (!_interactive) return;
        var parts = _order.Select(track =>
        {
            var (symbol, description) = _states[track.Label] switch
            {
                TrackState.Transcribing => ("●", "transcribing"),
                TrackState.Hearing => ("◐", "hearing audio"),
                _ => ("○", "listening"),
            };
            return $"[{track.Colour}]{symbol} {track.Label}[/] [grey]{description}[/]";
        });
        AnsiConsole.Markup(string.Join("   ", parts));
        _statusVisible = true;
    }
}
