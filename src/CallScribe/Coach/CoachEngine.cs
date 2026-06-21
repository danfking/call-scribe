using System.Threading.Channels;
using CallScribe.Coach.Memory;
using CallScribe.Transcription;

namespace CallScribe.Coach;

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

    private readonly IAdvisor _advisor;
    private readonly IMemoryStore? _memory;
    private readonly string _meetingId;
    private readonly AdviceFilter _adviceFilter = new();
    private readonly Channel<CaptionEvent> _inbox =
        Channel.CreateUnbounded<CaptionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<CaptionEvent> _context = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Raised when the advisor decides advice is warranted. Fires on the loop
    /// task, never on the caption thread.</summary>
    public event Action<AdviceEvent>? AdviceEmitted;

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
            // stop captioning or advice).
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

            try
            {
                var advice = await _advisor.ConsiderAsync(_context, caption, _cts.Token).ConfigureAwait(false);
                if (advice is { } a && _adviceFilter.ShouldEmit(a.Text, DateTime.Now))
                {
                    AdviceEmitted?.Invoke(a);
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
