using System.Runtime.InteropServices;
using NAudio.Dmo;
using NAudio.Wave;

namespace CallScribe.Audio;

/// <summary>
/// Pulls acoustic-echo-cancelled 16 kHz mono PCM from the Windows Voice Capture
/// DSP (CWMAudioAEC DMO) in source mode. The DMO opens the microphone and the
/// speaker devices itself and time-aligns them internally, so we never feed it
/// input: we just pull cleaned microphone audio out with ProcessOutput.
///
/// Implements IWaveIn so it slots into the existing CaptureTrack plumbing in
/// place of a WaveInEvent or loopback capture.
/// </summary>
public sealed class VoiceCaptureAecSource : IWaveIn
{
    // Verified COM identifiers (see docs/research/cwmaudioaec-source-mode-recipe.md).
    private static readonly Guid ClsidCWMAudioAec = new("745057C7-F353-4F2D-A7EE-58434477730E");
    private static readonly Guid WmaaecmaFmtid = new("6F52C567-0360-4BD2-9617-CCBF1421C939");

    // Property pids on the shared fmtid above (verified from wmcodecdsp.h).
    private const int PidSystemMode = 2;   // VT_I4  : 0 = SINGLE_CHANNEL_AEC
    private const int PidSourceMode = 3;   // VT_BOOL: source mode on
    private const int PidDeviceIndexes = 4; // VT_I4 : (spk<<16)|(mic&0xFFFF). Skipped for defaults.
    private const int PidFeatureMode = 5;   // VT_BOOL: must be on to set the FEATR_* keys below
    private const int PidFeatrNs = 8;       // VT_I4  : noise-suppression level
    private const int PidFeatrAes = 10;     // VT_I4  : residual echo suppression (0 off, 1, 2 most)

    private const int SingleChannelAec = 0;
    private const int SFalse = 1; // HRESULT S_FALSE: success with no data this round.

    // Output: 16 kHz, 16-bit, mono PCM, matching Whisper's preferred format.
    public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);

    /// <summary>Residual echo suppressor level: 0 = plain linear AEC (safest for
    /// double-talk, leaves a quieter residual for the text filter to catch), 1, or 2
    /// (most aggressive: cancels the far side fully but can clip the near-end voice
    /// while the far side talks). Set before StartRecording.</summary>
    public int EchoSuppressionLevel { get; set; } = 1;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private readonly int _micWaveInIndex;
    private readonly int _spkWaveOutIndex;
    private readonly bool _setDeviceIndexes;

    private object? _dmoCoClass;
    private IMediaObjectAec? _mediaObject;
    private IPropertyStoreAec? _props;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// Construct with default communications devices. The DMO picks the default
    /// comms mic and speaker when DEVICE_INDEXES is not set.
    /// </summary>
    public VoiceCaptureAecSource()
    {
        _setDeviceIndexes = false;
    }

    /// <summary>
    /// Construct with explicit winmm (WaveIn / WaveOut) device ordinals. These are
    /// NOT the Core Audio / MMDevice indices: the caller must map endpoints to
    /// winmm ordinals before passing them here. Pass -1 for either to let the DMO
    /// choose the default for that slot.
    /// </summary>
    public VoiceCaptureAecSource(int micWaveInIndex, int spkWaveOutIndex)
    {
        _micWaveInIndex = micWaveInIndex;
        _spkWaveOutIndex = spkWaveOutIndex;
        _setDeviceIndexes = true;
    }

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "AEC-Capture",
        };
        // The DMO wants an MTA apartment. STA is the classic way to get hangs.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void StopRecording()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(3000);
        _thread = null;
    }

    // Runs on the dedicated MTA thread: init the DMO, then pull cleaned audio
    // until told to stop. All COM work for the DMO happens on this one thread.
    private void Run()
    {
        Exception? stoppedError = null;
        try
        {
            InitDmo();
            PullLoop();
        }
        catch (Exception ex)
        {
            stoppedError = ex;
        }
        finally
        {
            TeardownDmo();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(stoppedError));
        }
    }

    private void InitDmo()
    {
        // Create the DMO and grab both interface views off the same RCW. Each cast
        // is a QueryInterface against the one underlying COM object.
        var type = Type.GetTypeFromCLSID(ClsidCWMAudioAec, throwOnError: true)!;
        _dmoCoClass = Activator.CreateInstance(type);
        _mediaObject = (IMediaObjectAec)_dmoCoClass!;
        _props = (IPropertyStoreAec)_dmoCoClass!;

        // Configure BEFORE SetOutputType. Order matters: source mode, then system
        // mode, then (optionally) device indexes.
        SetBool(PidSourceMode, true);
        SetInt(PidSystemMode, SingleChannelAec);
        if (_setDeviceIndexes)
        {
            var packed = (_spkWaveOutIndex << 16) | (_micWaveInIndex & 0xFFFF);
            SetInt(PidDeviceIndexes, packed);
        }

        // Optionally turn on the residual echo suppressor (and light noise suppression)
        // to clean up what plain AEC leaves behind. FEATURE_MODE must be set before the
        // FEATR_* keys. Level 0 leaves plain linear AEC, which is the safest for
        // double-talk because it never gates the near-end voice.
        if (EchoSuppressionLevel > 0)
        {
            SetBool(PidFeatureMode, true);
            SetInt(PidFeatrAes, EchoSuppressionLevel);
            SetInt(PidFeatrNs, 1);
        }

        // Output type = PCM 16 kHz / 16-bit / mono. MoInitMediaType allocates the
        // format block (cbFormat/pbFormat) that SetWaveFormat then fills, the same
        // way NAudio's own internal helper does it. We free that block afterwards;
        // the DMO copies what it needs during SetOutputType.
        var mt = new DmoMediaType();
        DmoInterop.MoInitMediaType(ref mt, Marshal.SizeOf<WaveFormat>());
        try
        {
            mt.SetWaveFormat(WaveFormat);
            var hr = _mediaObject.SetOutputType(0, ref mt, 0);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            DmoInterop.MoFreeMediaType(ref mt);
        }

        var allocHr = _mediaObject.AllocateStreamingResources();
        if (allocHr < 0)
        {
            Marshal.ThrowExceptionForHR(allocHr);
        }
    }

    private void PullLoop()
    {
        // Size the output buffer from the DMO. Fall back to ~200 ms of 16-bit mono
        // (16000 * 2 / 5 bytes) when the DMO reports no fixed size.
        var sizeHr = _mediaObject!.GetOutputSizeInfo(0, out var size, out _);
        if (sizeHr < 0 || size <= 0)
        {
            size = 16000 * 2 / 5;
        }

        var managed = new byte[size];
        // Reuse one output buffer (and its internal MediaBuffer) for every round.
        var arr = new DmoOutputDataBuffer[1];
        arr[0] = new DmoOutputDataBuffer(size);
        try
        {
            while (_running)
            {
                var hr = ProcessOutputOnce(arr);

                if (hr == SFalse || arr[0].Length == 0)
                {
                    // Nothing produced this round: sleep briefly and poll again.
                    Thread.Sleep(10);
                    continue;
                }

                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                // Drain the produced audio, then keep draining while the DMO says
                // more is buffered (the INCOMPLETE flag, surfaced as MoreDataAvailable).
                while (true)
                {
                    EnsureManagedCapacity(ref managed, arr[0].Length);
                    arr[0].RetrieveData(managed, 0);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(managed, arr[0].Length));

                    if (!arr[0].MoreDataAvailable)
                    {
                        break;
                    }

                    hr = ProcessOutputOnce(arr);
                    if (hr == SFalse || arr[0].Length == 0)
                    {
                        break;
                    }
                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }
            }
        }
        finally
        {
            arr[0].Dispose();
        }
    }

    // One ProcessOutput call on the single output stream. The internal MediaBuffer
    // is reused, so we must reset its valid length to 0 first: otherwise the DMO
    // sees a full buffer with no room and returns E_FAIL. Returns the HRESULT.
    private int ProcessOutputOnce(DmoOutputDataBuffer[] arr)
    {
        arr[0].MediaBuffer.SetLength(0);
        return _mediaObject!.ProcessOutput(DmoProcessOutputFlags.None, 1, arr, out _);
    }

    private static void EnsureManagedCapacity(ref byte[] managed, int needed)
    {
        if (managed.Length < needed)
        {
            managed = new byte[needed];
        }
    }

    private void TeardownDmo()
    {
        try
        {
            _mediaObject?.Flush();
            _mediaObject?.FreeStreamingResources();
        }
        catch
        {
            // Best-effort cleanup: if the DMO never fully initialised, ignore.
        }

        // Release the interface views before the coclass.
        if (_props is not null)
        {
            Marshal.ReleaseComObject(_props);
            _props = null;
        }
        if (_mediaObject is not null)
        {
            Marshal.ReleaseComObject(_mediaObject);
            _mediaObject = null;
        }
        if (_dmoCoClass is not null)
        {
            Marshal.ReleaseComObject(_dmoCoClass);
            _dmoCoClass = null;
        }
    }

    private void SetInt(int pid, int value)
    {
        var key = new PropertyKeyAec { fmtid = WmaaecmaFmtid, pid = pid };
        var pv = PropVariantAec.FromInt32(value);
        Marshal.ThrowExceptionForHR(_props!.SetValue(ref key, ref pv));
        // The DMO's property store applies values immediately and returns E_NOTIMPL
        // from Commit. Calling SetValue alone is the documented pattern (see the
        // AEC C++ sample and the Kinect C# driver), so we do not Commit here.
    }

    private void SetBool(int pid, bool value)
    {
        var key = new PropertyKeyAec { fmtid = WmaaecmaFmtid, pid = pid };
        var pv = PropVariantAec.FromBool(value);
        Marshal.ThrowExceptionForHR(_props!.SetValue(ref key, ref pv));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRecording();
        // TeardownDmo already ran on the capture thread via Run's finally block.
        // If StartRecording was never called, there is nothing to release.
    }
}
