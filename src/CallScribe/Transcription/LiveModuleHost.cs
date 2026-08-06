namespace CallScribe.Transcription;

/// <summary>Holds the dashboard's live modules and tracks which one owns the slot below the
/// transcript. <see cref="LiveStatusDisplay"/> owns one of these. Switching only changes the
/// visible module; pausing the hidden ones is each module's own concern, via
/// <see cref="ILiveModule.SetActive"/>. The host itself does no rendering; it just forwards the
/// active module's repaint and narration signals to the display.</summary>
public sealed class LiveModuleHost
{
    private readonly Lock _lock = new();
    private readonly List<ILiveModule> _modules = [];
    private ILiveModule? _active;

    /// <summary>Raised when the active module changes or a registered module asks for a repaint.</summary>
    public event Action? Changed;

    /// <summary>Raised with a ready-to-print line for the non-interactive (redirected) fallback.</summary>
    public event Action<string>? Narrated;

    public IReadOnlyList<ILiveModule> Modules
    {
        get { lock (_lock) { return [.. _modules]; } }
    }

    public ILiveModule? Active
    {
        get { lock (_lock) { return _active; } }
    }

    /// <summary>Register a module (idempotent by id). Its repaint/narration signals are forwarded
    /// to the display. Registration does not activate the module; call <see cref="SetActive(string?)"/>.</summary>
    public void Register(ILiveModule module)
    {
        lock (_lock)
        {
            if (_modules.Any(m => m.Id.Equals(module.Id, StringComparison.OrdinalIgnoreCase))) return;
            _modules.Add(module);
        }
        module.Changed += OnModuleChanged;
        module.Narrated += OnModuleNarrated;
    }

    private void OnModuleChanged() => Changed?.Invoke();
    private void OnModuleNarrated(string line) => Narrated?.Invoke(line);

    /// <summary>Make the module with this id active (case-insensitive). Null or an unknown id clears
    /// the slot. Returns true when the active module actually changed.</summary>
    public bool SetActive(string? id)
    {
        ILiveModule? next;
        lock (_lock)
        {
            next = id is null ? null
                : _modules.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(next, _active)) return false;
        }
        Switch(next);
        return true;
    }

    /// <summary>Cycle to the next registered module (wrapping). No-op when nothing is registered.</summary>
    public void Cycle()
    {
        ILiveModule? next;
        lock (_lock)
        {
            if (_modules.Count == 0) return;
            var i = _active is null ? -1 : _modules.IndexOf(_active);
            next = _modules[(i + 1) % _modules.Count];
            if (ReferenceEquals(next, _active)) return;
        }
        Switch(next);
    }

    private void Switch(ILiveModule? next)
    {
        lock (_lock)
        {
            _active?.SetActive(false);
            _active = next;
            _active?.SetActive(true);
        }
        Changed?.Invoke();
    }

    /// <summary>Drain every module's in-flight work at end of meeting. Best-effort per module.</summary>
    public async Task CompleteAllAsync()
    {
        ILiveModule[] mods;
        lock (_lock) { mods = [.. _modules]; }
        foreach (var m in mods)
        {
            try { await m.CompleteAsync().ConfigureAwait(false); }
            catch { /* one module failing to drain must not block the others */ }
        }
    }

    /// <summary>Dispose every module. Best-effort per module.</summary>
    public void DisposeAll()
    {
        ILiveModule[] mods;
        lock (_lock) { mods = [.. _modules]; }
        foreach (var m in mods)
        {
            try { m.Dispose(); }
            catch { /* best-effort */ }
        }
    }
}
