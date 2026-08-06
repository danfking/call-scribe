using System.Threading.Channels;
using CallScribe.Coach.Memory;
using CallScribe.Transcription;

namespace CallScribe.Coach;

/// <summary>What the coach is doing right now, for the live status indicator. The loop is
/// reactive per-utterance, so these are the states a user can perceive: waiting for speech,
/// working on the latest utterance, or done with it having chosen to stay silent.</summary>
public enum CoachActivity
{
    /// <summary>Idle between utterances, or just finished by emitting advice (the advice
    /// line is the signal).</summary>
    Listening,

    /// <summary>Processing a caption: memory recall plus the fast-model call.</summary>
    Thinking,

    /// <summary>Considered the latest utterance and produced nothing (model declined, or the
    /// advice repeated something recent and was suppressed).</summary>
    Quiet,
}

/// <summary>Watches the live caption stream and runs an Observe → Reflect → Plan → Act
/// loop, emitting advice asynchronously so transcription is never blocked.
///
/// Observe runs on whatever thread feeds <see cref="Observe"/> (a caption worker), and
/// only enqueues. A single background task drains the queue, maintains the rolling
/// context window, and calls the advisor — so the advisor sees captions in order and
/// the context list needs no locking. Phase 1 uses a stub advisor; the loop, threading
/// model, and seam are the real deliverable.</summary>
public sealed class CoachEngine : IDisposable
{
    private const int ContextWindow = 50;
    // How many recent pieces of advice to feed back to the advisor as "already said, don't repeat".
    private const int RecentAdviceWindow = 15;

    private readonly IAdvisor _advisor;
    private readonly IMemoryStore? _memory;
    private readonly string _meetingId;
    // Suppress repeated advice for the whole meeting, not just a short window: re-defining a term
    // discussed minutes ago (because it lingers in the context window) was the failure mode.
    private readonly AdviceFilter _adviceFilter = new(retentionWindow: TimeSpan.FromHours(8));
    private readonly Channel<CaptionEvent> _inbox =
        Channel.CreateUnbounded<CaptionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<CaptionEvent> _context = [];
    // Advice already shown this meeting (loop-task only, like _context), fed to the advisor so it
    // won't repeat or rephrase a point it has already made.
    private readonly List<string> _recentAdvice = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    // When false, the loop still persists each caption but skips the advisor call (no model work).
    // The coach panel module flips this on the meeting's active-panel switch, so a hidden coach
    // costs nothing while the transcript it backs stays complete. Volatile: set from the UI thread,
    // read on the loop task.
    private volatile bool _adviceActive = true;

    /// <summary>Raised when the advisor decides advice is warranted. Fires on the loop
    /// task, never on the caption thread.</summary>
    public event Action<AdviceEvent>? AdviceEmitted;

    /// <summary>Raised when the coach's activity state changes (for the live status indicator):
    /// Thinking while it works the latest utterance, then Listening (advised) or Quiet (nothing
    /// to add). Fires on the loop task, never on the caption thread.</summary>
    public event Action<CoachActivity>? ActivityChanged;

    public CoachEngine(IAdvisor advisor, IMemoryStore? memory = null, string? meetingId = null)
    {
        _advisor = advisor;
        _memory = memory;
        _meetingId = meetingId ?? "session";
        _loop = Task.Run(ProcessAsync);
    }

    /// <summary>Feed a caption into the loop. Safe to call from caption worker threads;
    /// returns immediately — the advisor work runs on the loop task.</summary>
    public void Observe(CaptionEvent caption) => _inbox.Writer.TryWrite(caption);

    /// <summary>Show or hide the advice work. When inactive the loop still persists captions (so the
    /// live transcript stays complete) but makes no advisor/model call. Safe to call from any thread.</summary>
    public void SetAdviceActive(bool active) => _adviceActive = active;

    private async Task ProcessAsync()
    {
        // One caption at a time, in order. At speaking pace the live engine emits a
        // caption every ~1.5-5s and the fast model replies in ~1-3s, so the queue
        // stays shallow; if a future model proves too slow, add debouncing here.
        await foreach (var caption in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _context.Add(caption);
            if (_context.Count > ContextWindow)
            {
                _context.RemoveRange(0, _context.Count - ContextWindow);
            }

            // Persist to the realtime hypertable (best effort: a memory hiccup must not
            // stop captioning or advice). Persistence is unconditional: it backs the live
            // transcript, so it runs even while the coach panel is hidden.
            if (_memory != null)
            {
                try
                {
                    await _memory.InsertSegmentAsync(
                        _meetingId, caption.At, caption.SpeakerName, caption.Caption, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* swallow: persistence is non-critical to the live loop */ }
            }

            // Hidden: persist only, no model work. The context window above is still maintained so
            // advice has history to draw on the moment the panel is shown again.
            if (!_adviceActive) continue;

            ActivityChanged?.Invoke(CoachActivity.Thinking);
            try
            {
                var advice = await _advisor.ConsiderAsync(_context, caption, _recentAdvice, _cts.Token).ConfigureAwait(false);
                if (advice is { } a && _adviceFilter.ShouldEmit(a.Text, DateTime.Now))
                {
                    AdviceEmitted?.Invoke(a);
                    _recentAdvice.Add(a.Text);
                    if (_recentAdvice.Count > RecentAdviceWindow)
                    {
                        _recentAdvice.RemoveRange(0, _recentAdvice.Count - RecentAdviceWindow);
                    }
                    ActivityChanged?.Invoke(CoachActivity.Listening); // advised; the line is the signal
                }
                else
                {
                    ActivityChanged?.Invoke(CoachActivity.Quiet); // declined or suppressed duplicate
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AdviceEmitted?.Invoke(new AdviceEvent(
                    DateTime.Now, AdviceKind.Warning, $"coach error: {ex.Message}", "coach"));
                ActivityChanged?.Invoke(CoachActivity.Listening);
            }
        }
    }

    /// <summary>Stop accepting captions and drain any in-flight advice.</summary>
    public async Task CompleteAsync()
    {
        _inbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _inbox.Writer.TryComplete();
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
    }
}
