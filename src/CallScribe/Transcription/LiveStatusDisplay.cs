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
    private readonly bool _interactive = ConsoleIsUsable();
    private bool _statusVisible;
    private string _pad = "";
    private int _padWidth = -1;

    // True only when output is not redirected and a real console window exists.
    // On a detached or service-spawned process Console.WindowWidth throws, so we
    // treat any failure as "no usable console" and disable the status line.
    private static bool ConsoleIsUsable()
    {
        if (Console.IsOutputRedirected) return false;
        try
        {
            _ = Console.WindowWidth;
            return true;
        }
        catch
        {
            return false;
        }
    }

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
        if (width != _padWidth)
        {
            _pad = new string(' ', width);
            _padWidth = width;
        }
        Console.Write('\r');
        Console.Write(_pad);
        Console.Write('\r');
        _statusVisible = false;
    }

    private void DrawStatus()
    {
        if (!_interactive) return;
        // Plain ASCII only: the console codepage may not render geometric symbols,
        // which show up as stray "?" characters. The colour carries the track.
        var parts = _order.Select(track =>
        {
            var description = _states[track.Label] switch
            {
                TrackState.Transcribing => "transcribing",
                TrackState.Hearing => "hearing audio",
                _ => "listening",
            };
            return $"[{track.Colour}]{track.Label}[/] [grey]{description}[/]";
        });
        AnsiConsole.Markup("[grey]status:[/] " + string.Join("   ", parts));
        _statusVisible = true;
    }
}
