# CWMAudioAEC Voice Capture DSP in source mode from C#: implementation recipe

> Code-ready recipe for using the Windows Voice Capture DSP (CWMAudioAEC DMO) in
> **source mode** from .NET to capture acoustic-echo-cancelled microphone audio,
> using the speaker/render stream as the echo reference. Targets NAudio 2.3.0
> (NAudio.Dmo types ship inside NAudio.Wasapi.dll) and outputs 16 kHz mono PCM
> for Whisper.
>
> Companion to `echo-suppression-aec.md` (the design decision). This is the spec
> for Phase 1 of that document.
>
> **Verification note:** GUIDs and pids below were verified against the installed
> Windows 11 SDK header `wmcodecdsp.h` (10.0.26100.0) and cross-checked against
> Microsoft Learn, the Windows-classic-samples C++ AEC sample, and a decompiled
> Microsoft.Kinect C# driver of the same DMO surface. Where a value could not be
> verified it is called out explicitly rather than invented.

---

## 0. How source mode works (the mental model)

In **source mode** the DMO owns the devices. You tell it which mic and which
speaker to use (by index), set the output format, and then just *pull* cleaned
audio out with `ProcessOutput`. You never call `ProcessInput`: the DMO reads the
capture device and the render device itself, time-aligns them internally, and
hands you echo-cancelled mic audio. This sidesteps call-scribe's two-clock
alignment problem entirely (see `echo-suppression-aec.md` section 7).

Contrast with **filter mode**, where you feed mic on input stream 0 and the
reference on input stream 1 via `ProcessInput` and own the alignment yourself.
We are not using filter mode.

The DMO exposes exactly two COM interfaces: `IMediaObject` (streaming) and
`IPropertyStore` (configuration). It does **not** implement `IMFTransform`.
Source: https://learn.microsoft.com/en-us/windows/win32/medfound/voicecapturedmo

---

## 1. COM identifiers (verified GUIDs)

| Item | GUID / value | Verified from |
|---|---|---|
| **CLSID_CWMAudioAEC** | `745057C7-F353-4F2D-A7EE-58434477730E` | wmcodecdsp.h (SDK 10.0.26100.0); DirectN `MFConstants.cs`; Kinect driver |
| **IID_IMediaObject** | `D8AD0F58-5494-4102-97C5-EC798E59BCF4` | mediaobj.idl; NAudio `IMediaObject.cs` |
| **IID_IPropertyStore** | `886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99` | propsys.h; NAudio `IPropertyStore.cs` |
| **DLL** | `Mfwmaaec.dll` | Learn: voicecapturedmo |
| Header / lib | `Wmcodecdsp.h` / `wmcodecdspuuid.lib` | Learn |

Create with `CoCreateInstance(CLSID_CWMAudioAEC, ...)` (or, from C#, via a
`[ComImport]` coclass / `Type.GetTypeFromCLSID` + `Activator.CreateInstance`).

---

## 2. Property keys (verified PROPERTYKEYs)

Every `MFPKEY_WMAAECMA_*` key shares one fmtid GUID:

```
fmtid = {6F52C567-0360-4BD2-9617-CCBF1421C939}
```

The pid is `PID_FIRST_USABLE` (which is **2**, per SDK `propkeydef.h`) plus the
header offset. Resolved integer pids (verified from wmcodecdsp.h lines 3292-3311):

| Property | header offset | **pid** | VARTYPE | Value to set |
|---|---|---|---|---|
| MFPKEY_WMAAECMA_SYSTEM_MODE | +0 | **2** | VT_I4 | `0` (SINGLE_CHANNEL_AEC) |
| MFPKEY_WMAAECMA_DMO_SOURCE_MODE | +1 | **3** | VT_BOOL | `VARIANT_TRUE` |
| MFPKEY_WMAAECMA_DEVICE_INDEXES | +2 | **4** | VT_I4 | packed (see below) |
| MFPKEY_WMAAECMA_FEATURE_MODE | +3 | **5** | VT_BOOL | `VARIANT_TRUE` to allow setting FEATR_* |
| MFPKEY_WMAAECMA_FEATR_NS | +6 | **8** | VT_I4 | noise-suppression level (e.g. 1) |
| MFPKEY_WMAAECMA_FEATR_AGC | +7 | **9** | VT_BOOL | auto-gain on/off |
| MFPKEY_WMAAECMA_FEATR_AES | +8 | **10** | VT_I4 | acoustic echo suppression (0/1/2) |
| MFPKEY_WMAAECMA_FEATR_CENTER_CLIP | +10 | **12** | VT_BOOL | center-clipping on/off |
| MFPKEY_WMAAECMA_MICARRAY_DESCPTR | +14 | **16** | VT_BLOB | mic-array geometry (array modes only) |

**Caveat on AES vs NS ordering:** the task listed AES before NS, but the header
puts NS at +6 (pid 8) and AES at +8 (pid 10). The resolved pids above are the
absolute values to put in the PROPERTYKEY.

**MFPKEY_WMAAECMA_RETAIN_FORMAT: could not be verified.** It does not appear in
wmcodecdsp.h, anywhere under the SDK include tree, or on Microsoft Learn. Treat
it as not a real Voice Capture DSP key. Do not guess a pid for it. (If you need
to keep the upstream device format, that is governed by source mode + the output
type you set, not by a separate "retain format" key.)

### 2a. AEC_SYSTEM_MODE enum (verified)

From Learn `ne-wmcodecdsp-aec_system_mode` and DirectN `AEC_SYSTEM_MODE.cs`:

| Member | value | Use |
|---|---|---|
| **SINGLE_CHANNEL_AEC** | **0** | **plain single-channel AEC. This is what we want.** |
| ADAPTIVE_ARRAY_ONLY | 1 | reserved |
| OPTIBEAM_ARRAY_ONLY | 2 | mic-array beamforming, no AEC |
| ADAPTIVE_ARRAY_AND_AEC | 3 | reserved |
| OPTIBEAM_ARRAY_AND_AEC | 4 | mic array + AEC |
| SINGLE_CHANNEL_NSAGC | 5 | NS/AGC only, no AEC |
| MODE_NOT_SET | 6 | uninitialised default; do not set |

**Correction to the task's assumption:** OPTIBEAM_ARRAY_AND_AEC is **4**, not 3
(slot 3 is the reserved ADAPTIVE_ARRAY_AND_AEC). SINGLE_CHANNEL_AEC = 0 confirmed.
For call-scribe (single mic, no array), use **0**.

### 2b. DEVICE_INDEXES packing (verified from the C++ sample)

`MFPKEY_WMAAECMA_DEVICE_INDEXES` is a single VT_I4 with the speaker index in the
high word and the mic index in the low word. Verbatim from `AecSDKDemo.cpp`:

```c
pvDeviceId.vt   = VT_I4;
pvDeviceId.lVal = (unsigned long)(iSpkDevIdx << 16) + (unsigned long)(0x0000ffff & iMicDevIdx);
```

So: **packed = (speakerIndex << 16) | (micIndex & 0xFFFF)**. The Kinect C# driver
does the same with masks `0xFFFF0000` / `0x0000FFFF`.

**The numbering trap:** these are **WaveIn / WaveOut (winmm) device ordinals**,
not Core Audio (WASAPI / MMDevice) endpoint indices. The mic index is the
`waveInGetDevID`-space ordinal (enumerate with `waveInGetNumDevs` /
`waveInGetDevCaps`); the speaker index is the `waveOutGetNumDevs` /
`waveOutGetDevCaps` ordinal. If you pick a device with the Core Audio enumerator
(NAudio `MMDeviceEnumerator`) you must map it back to the winmm ordinal, because
the two enumerations do not share an index space. Using the wrong space silently
points AEC at the wrong reference and the cancellation just fails.

**Defaults / sentinel:** pass `-1` for either slot to let the DMO choose the
default device. If you do not set DEVICE_INDEXES at all, the DMO uses the default
communications devices. Easiest first cut: `-1, -1` to validate the path, then
move to explicit winmm ordinals once it works.

Sources:
https://learn.microsoft.com/en-us/windows/win32/medfound/mfpkey-wmaaecma-device-indexesproperty ,
https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/multimedia/audio/aecmicarray/AecSDKDemo.cpp

---

## 3. Initialization and run sequence (source mode)

Order matters: source mode and system mode go in **before** `SetOutputType`.

1. **CoInitialize MTA**, then **CoCreateInstance** the DMO (`CLSID_CWMAudioAEC`).
2. **QueryInterface** the same object for `IPropertyStore`.
3. **Set `MFPKEY_WMAAECMA_DMO_SOURCE_MODE = VARIANT_TRUE`** (VT_BOOL). Source
   mode is the documented default, but set it explicitly.
4. **Set `MFPKEY_WMAAECMA_SYSTEM_MODE = 0`** (SINGLE_CHANNEL_AEC, VT_I4). This is
   the one mandatory property.
5. **Set `MFPKEY_WMAAECMA_DEVICE_INDEXES`** = packed `(spk<<16)|(mic&0xFFFF)`
   (VT_I4). Optional; omit or use -1/-1 for defaults.
6. (Optional features: set `FEATURE_MODE = VARIANT_TRUE`, then `FEATR_NS`,
   `FEATR_AGC`, `FEATR_AES`, `FEATR_CENTER_CLIP`.)
7. **`SetOutputType(0, &mt, 0)`** with PCM 16 kHz, 16-bit, mono. **Never call
   `SetInputType` in source mode.**
8. **`AllocateStreamingResources()`**.
9. **Loop `ProcessOutput`** pulling cleaned audio. No `ProcessInput`.

The output media type, verbatim from the Learn "Using the Voice Capture DSP"
page (this is the exact WAVEFORMATEX we want):

```c
DMO_MEDIA_TYPE mt;
mt.majortype = MEDIATYPE_Audio;        // {73647561-0000-0010-8000-00AA00389B71}
mt.subtype   = MEDIASUBTYPE_PCM;       // {00000001-0000-0010-8000-00AA00389B71}
mt.lSampleSize = 0;
mt.bFixedSizeSamples = TRUE;
mt.bTemporalCompression = FALSE;
mt.formattype = FORMAT_WaveFormatEx;   // {05589F81-C356-11CE-BF01-00AA0055595A}
// WAVEFORMATEX:
wFormatTag = WAVE_FORMAT_PCM(1); nChannels = 1; nSamplesPerSec = 16000;
nAvgBytesPerSec = 32000; nBlockAlign = 2; wBitsPerSample = 16; cbSize = 0;
hr = pDMO->SetOutputType(0, &mt, 0);
```

Supported rates are 8000 / 11025 / 16000 / 22050 Hz; 16 kHz mono matches Whisper
and is what we use.

### The ProcessOutput pull loop and "no data yet"

`DMO_OUTPUT_DATA_BUFFER` is `{ IMediaBuffer pBuffer; DWORD dwStatus; REFERENCE_TIME rtTimestamp; REFERENCE_TIME rtTimelength; }`.

Two distinct "nothing produced this round" signals:
- `ProcessOutput` returns **S_FALSE** (HRESULT 0x00000001, not a failure), and/or
- the output buffer's valid length comes back **0**.

The flag **`DMO_OUTPUT_DATA_BUFFERF_INCOMPLETE = 0x01000000`** means the opposite:
"more data is buffered, call me again immediately." Drain while it is set.
Verbatim shape from `AecSDKDemo.cpp`:

```c
do {
    OutputBufferStruct.dwStatus = 0;
    hr = pDMO->ProcessOutput(0, 1, &OutputBufferStruct, &dwStatus);
    if (hr == S_FALSE) { cbProduced = 0; }            // no data this round
    else { outputBuffer.GetBufferAndLength(NULL, &cbProduced); }
    // ... consume cbProduced bytes ...
} while (OutputBufferStruct.dwStatus & DMO_OUTPUT_DATA_BUFFERF_INCOMPLETE);
```

Because source mode is real-time, when nothing is produced you **sleep briefly
(~10 ms) and poll again**. Size your output buffer from
`GetOutputSizeInfo(0).Size` (plus alignment).

---

## 4. What NAudio gives you, and where you drop to raw COM

NAudio 2.3.0's `NAudio.Dmo` types (in **NAudio.Wasapi.dll**) cover the *streaming
plumbing* but **not** DMO configuration. Here is the precise split.

**NAudio helps (reuse these public types):**
- `NAudio.Dmo.IMediaObject` (`[ComImport]`, IID d8ad0f58-...): the interface
  definition. Public, so you can cast your coclass to it.
- `NAudio.Dmo.DmoMediaType` with `SetWaveFormat(WaveFormat)` /
  `GetWaveFormat()`: builds the `DMO_MEDIA_TYPE` for PCM (sets MEDIATYPE_Audio,
  MEDIASUBTYPE_PCM, FORMAT_WaveFormatEx, marshals the WAVEFORMATEX).
- `NAudio.Dmo.MediaBuffer` (implements `IMediaBuffer`): unmanaged buffer with
  `RetrieveData(byte[], offset)` to copy cleaned audio out.
- `NAudio.Dmo.DmoOutputDataBuffer` struct: wraps `DMO_OUTPUT_DATA_BUFFER`. Has
  `MediaBuffer`, `Length`, `StatusFlags`, `RetrieveData(...)`, and crucially
  `MoreDataAvailable` (which tests the INCOMPLETE flag for you).
- `NAudio.Dmo.MediaObjectSizeInfo` (`Size`, `MaxLookahead`, `Alignment`): size
  your output buffer from this.
- WASAPI-side `NAudio.CoreAudioApi.Interfaces.IPropertyStore`,
  `NAudio.CoreAudioApi.PropVariant`, `NAudio.CoreAudioApi.PropertyKey`: these are
  **public** and reusable. You do not have to redefine them.

**Where NAudio does NOT help (you do this yourself):**
- **`MediaObject`'s constructor is `internal`** (`internal MediaObject(IMediaObject)`),
  and it holds the `IMediaObject` in a **private** field with no accessor. So you
  cannot `new MediaObject(...)` from your own assembly, and you cannot pull the
  raw pointer out of an existing one. Practical consequence: don't route through
  NAudio's `MediaObject` wrapper for the AEC DMO. Declare/reuse the public
  `IMediaObject` interface and call `ProcessOutput` / `AllocateStreamingResources`
  / `SetOutputType` on it directly.
- **No factory for an arbitrary DMO CLSID** on 2.x. You `CoCreateInstance` the
  CWMAudioAEC coclass yourself.
- **IPropertyStore is not wired to any DMO.** You QueryInterface it yourself.

**The pattern NAudio's own `DmoResampler` proves** (file:
`NAudio.Wasapi/Dmo/ResamplerMediaObject.cs`): declare a `[ComImport]` coclass,
`new` it, then cast the *same RCW* to each interface (each cast is a
QueryInterface):

```csharp
[ComImport, Guid("745057C7-F353-4F2D-A7EE-58434477730E")]
class VoiceCaptureDmoCoClass { }

var dmo           = new VoiceCaptureDmoCoClass();          // or Activator.CreateInstance(Type.GetTypeFromCLSID(clsid))
var mediaObject   = (NAudio.Dmo.IMediaObject)dmo;          // streaming view
var propertyStore = (NAudio.CoreAudioApi.Interfaces.IPropertyStore)dmo; // config view (QI)
```

### Raw COM definitions you need (if not reusing NAudio's)

If you prefer self-contained interop instead of reusing NAudio's WASAPI types,
the three shapes are:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct PROPERTYKEY { public Guid fmtid; public int pid; }

// PROPVARIANT: use NAudio.CoreAudioApi.PropVariant, or the well-known 16-byte
// explicit-layout union (DirectN PropVariant.cs is a clean reference). For this
// DMO you only need VT_I4 (vt=3, store in lVal/intVal) and VT_BOOL
// (vt=11, VARIANT_TRUE = -1 / 0xFFFF as a short).

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    int GetCount(out int cProps);
    int GetAt(int iProp, out PROPERTYKEY pkey);
    int GetValue(ref PROPERTYKEY key, out PropVariant pv);
    int SetValue(ref PROPERTYKEY key, ref PropVariant pv);
    int Commit();
}
```

Reliable sources for these: NAudio
`NAudio.Wasapi/CoreAudioApi/PropVariant.cs`, `.../PropertyKey.cs`,
`.../Interfaces/IPropertyStore.cs`; and DirectN `Manual/PropVariant.cs`,
`Manual/IPropertyStore.cs`. The Kinect driver
(`vvvv/VL.Devices.Kinect/.../IPropertyStore.cs`) uses a simpler variant that
marshals the PROPVARIANT as a boxed `object` (`SetValue(ref PROPERTYKEY, ref object)`),
which works because the boxed int/bool carries the VARTYPE; that is an even
lighter option if you want to skip a hand-rolled PROPVARIANT struct.

NAudio file URLs:
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/MediaObject.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/IMediaObject.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/DmoOutputDataBuffer.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/IMediaBuffer.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/DmoMediaType.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/MediaObjectSizeInfo.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/ResamplerMediaObject.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/CoreAudioApi/PropVariant.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/CoreAudioApi/PropertyKey.cs
- https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/CoreAudioApi/Interfaces/IPropertyStore.cs

---

## 5. C# code sketch (compilable-shaped)

This reflects the real API shapes. It reuses NAudio's public `IMediaObject`,
`MediaBuffer`, and `DmoMediaType`, defines its own minimal `IPropertyStore` /
`PROPERTYKEY` / `PROPVARIANT`, runs on an MTA thread, and exposes cleaned audio
via a `DataAvailable` event in IWaveIn style. Treat it as a faithful skeleton,
not copy-paste-perfect code.

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Dmo;          // IMediaObject, MediaBuffer, DmoMediaType, DmoProcessOutputFlags
using NAudio.Wave;         // WaveFormat, WaveInEventArgs

namespace CallScribe.Audio
{
    /// <summary>
    /// Pulls acoustic-echo-cancelled 16 kHz mono PCM from the Windows Voice
    /// Capture DSP (CWMAudioAEC) in source mode. The DMO opens the mic and
    /// speaker devices itself and time-aligns them; we only pull output.
    /// </summary>
    public sealed class VoiceCaptureAecSource : IDisposable
    {
        // ---- COM identifiers (verified) ----
        static readonly Guid CLSID_CWMAudioAEC =
            new Guid("745057C7-F353-4F2D-A7EE-58434477730E");
        static readonly Guid WMAAECMA_FMTID =
            new Guid("6F52C567-0360-4BD2-9617-CCBF1421C939");

        // ---- Property pids (verified from wmcodecdsp.h) ----
        const int PID_SYSTEM_MODE    = 2;   // VT_I4  : 0 = SINGLE_CHANNEL_AEC
        const int PID_SOURCE_MODE    = 3;   // VT_BOOL: VARIANT_TRUE
        const int PID_DEVICE_INDEXES = 4;   // VT_I4  : (spk<<16)|(mic&0xFFFF)
        const int PID_FEATURE_MODE   = 5;   // VT_BOOL
        const int PID_FEATR_NS       = 8;   // VT_I4
        const int PID_FEATR_AGC      = 9;   // VT_BOOL
        const int PID_FEATR_AES      = 10;  // VT_I4
        const int PID_FEATR_CCLIP    = 12;  // VT_BOOL

        const int SINGLE_CHANNEL_AEC = 0;
        const uint DMO_OUTPUT_DATA_BUFFERF_INCOMPLETE = 0x01000000;
        const int  S_FALSE = 1;

        // Output: 16 kHz, 16-bit, mono PCM — exactly Whisper's preferred format.
        public WaveFormat WaveFormat { get; } = new WaveFormat(16000, 16, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;

        readonly int _micIndex;     // waveIn ordinal,  -1 = default
        readonly int _spkIndex;     // waveOut ordinal, -1 = default
        object _dmoCoClass;
        IMediaObject _mediaObject;
        IPropertyStore _props;
        Thread _thread;
        volatile bool _running;

        public VoiceCaptureAecSource(int micWaveInIndex = -1, int spkWaveOutIndex = -1)
        {
            _micIndex = micWaveInIndex;
            _spkIndex = spkWaveOutIndex;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "AEC-Capture" };
            _thread.SetApartmentState(ApartmentState.MTA);   // DMO wants MTA
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(2000);
        }

        void Run()
        {
            // 1. Create the DMO and grab both interface views off the same RCW.
            var type = Type.GetTypeFromCLSID(CLSID_CWMAudioAEC, throwOnError: true);
            _dmoCoClass  = Activator.CreateInstance(type);
            _mediaObject = (IMediaObject)_dmoCoClass;
            _props       = (IPropertyStore)_dmoCoClass;   // QueryInterface

            // 2. Configure BEFORE SetOutputType. Order matters.
            SetBool(PID_SOURCE_MODE, true);                       // source mode on
            SetInt (PID_SYSTEM_MODE, SINGLE_CHANNEL_AEC);         // plain AEC
            int packed = (_spkIndex << 16) | (_micIndex & 0xFFFF);
            SetInt (PID_DEVICE_INDEXES, packed);                  // winmm ordinals
            // Optional: gentle residual cleanup (see echo-suppression-aec.md).
            SetBool(PID_FEATURE_MODE, true);
            SetInt (PID_FEATR_AES, 1);                            // echo suppression
            SetInt (PID_FEATR_NS, 1);                             // light noise supp

            // 3. Output type = PCM 16k/16/mono. DmoMediaType builds the struct.
            var mt = new DmoMediaType();
            mt.SetWaveFormat(WaveFormat);
            try { _mediaObject.SetOutputType(0, ref mt, 0); }     // signature: see note
            finally { /* free mt's pbFormat per DmoMediaType helper */ }

            // 4. Allocate streaming resources, then pull.
            _mediaObject.AllocateStreamingResources();

            int outSize = 16000 * 2 / 10;   // ~100 ms of 16-bit mono; size from
                                            // GetOutputSizeInfo(0).Size in real code
            var buffer  = new MediaBuffer(outSize);
            var managed = new byte[outSize];

            while (_running)
            {
                var dataBuf = new DmoOutputDataBuffer(outSize);
                int hr = ProcessOutputOnce(dataBuf, out int produced);

                if (hr == S_FALSE || produced == 0)
                {
                    Thread.Sleep(10);       // nothing ready: poll again
                    dataBuf.Dispose();
                    continue;
                }

                do
                {
                    dataBuf.RetrieveData(managed, 0);
                    DataAvailable?.Invoke(this,
                        new WaveInEventArgs(managed, dataBuf.Length));
                }
                while (dataBuf.MoreDataAvailable &&   // INCOMPLETE flag set
                       ProcessOutputOnce(dataBuf, out produced) == 0 && produced > 0);

                dataBuf.Dispose();
            }

            _mediaObject.FreeStreamingResources();
        }

        // ProcessOutput on a single output stream. Returns HRESULT.
        int ProcessOutputOnce(DmoOutputDataBuffer dataBuf, out int produced)
        {
            dataBuf.StatusFlags = 0;
            var arr = new[] { dataBuf };
            int hr = _mediaObject.ProcessOutput(
                DmoProcessOutputFlags.None, 1, arr, out int _);
            produced = arr[0].Length;
            return hr;
        }

        // ---- property helpers ----
        void SetInt(int pid, int value)
        {
            var key = new PROPERTYKEY { fmtid = WMAAECMA_FMTID, pid = pid };
            var pv  = PROPVARIANT.FromInt32(value);     // vt = VT_I4 (3)
            Marshal.ThrowExceptionForHR(_props.SetValue(ref key, ref pv));
            _props.Commit();
        }

        void SetBool(int pid, bool value)
        {
            var key = new PROPERTYKEY { fmtid = WMAAECMA_FMTID, pid = pid };
            var pv  = PROPVARIANT.FromBool(value);      // vt = VT_BOOL (11)
            Marshal.ThrowExceptionForHR(_props.SetValue(ref key, ref pv));
            _props.Commit();
        }

        public void Dispose()
        {
            Stop();
            if (_props       != null) Marshal.ReleaseComObject(_props);
            if (_mediaObject != null) Marshal.ReleaseComObject(_mediaObject);
            if (_dmoCoClass  != null) Marshal.ReleaseComObject(_dmoCoClass);
        }
    }

    // ---- minimal interop (or reuse NAudio.CoreAudioApi equivalents) ----
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    // 16-byte VARIANT-shaped union; only VT_I4 and VT_BOOL are needed here.
    [StructLayout(LayoutKind.Explicit)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int    lVal;       // VT_I4
        [FieldOffset(8)] public short  boolVal;    // VT_BOOL (-1 = TRUE)
        public static PROPVARIANT FromInt32(int v) => new PROPVARIANT { vt = 3,  lVal = v };
        public static PROPVARIANT FromBool(bool v) => new PROPVARIANT { vt = 11, boolVal = (short)(v ? -1 : 0) };
    }
}
```

**Notes on the sketch (real-API caveats):**
- `IMediaObject.SetOutputType` and `ProcessOutput` signatures: take them from
  NAudio's `IMediaObject.cs`. `SetOutputType(int, ref DmoMediaType, int)` and
  `ProcessOutput(DmoProcessOutputFlags, int, DmoOutputDataBuffer[], out int)` are
  the 2.x shapes. If you reuse NAudio's interface, match its exact parameter
  marshalling rather than the simplified calls above.
- Size the output buffer from `GetOutputSizeInfo(0).Size` (rounded up to
  `Alignment`), not a hard-coded 100 ms.
- `DmoMediaType` allocates `pbFormat`; free it after `SetOutputType` (NAudio's
  type exposes the cleanup; don't leak the WAVEFORMATEX).
- The `DataAvailable` event fires on the MTA capture thread. Marshal to your
  consumer's thread/queue as call-scribe's pipeline expects.

---

## 6. Known pitfalls

- **Threading: use MTA, not STA.** Create a dedicated background thread and call
  `SetApartmentState(ApartmentState.MTA)` before `Start()`. Both the C++ guidance
  and the Kinect C# driver run this DMO on MTA. The CLR honours `ApartmentState`,
  so an explicit `CoInitializeEx` is not required, but the apartment must be MTA.
  Driving the DMO from a UI STA thread is the classic way to get hangs / wrong
  behaviour.
- **Device-index numbering mismatch (the big one).** DEVICE_INDEXES uses
  **WaveIn/WaveOut (winmm) ordinals**, while the rest of call-scribe selects
  devices through Core Audio / WASAPI (`MMDeviceEnumerator`). These index spaces
  are unrelated. You must map your chosen WASAPI endpoint back to its winmm
  ordinal (match on device friendly name / endpoint ID via `waveInGetDevCaps` /
  `waveOutGetDevCaps`) before packing. Get this wrong and AEC quietly cancels
  against the wrong reference. Start with -1/-1 (defaults) to prove the pipeline,
  then add the mapping.
- **Latency.** Source mode runs in real time and buffers internally; expect tens
  of ms of added latency and a short adaptive-filter convergence period at the
  start of a call before cancellation is fully effective.
- **Silence / no audio playing.** When nothing is on the render side, the
  reference is silence, AEC has nothing to cancel, and mic audio passes through
  largely untouched. That is correct and harmless. Unlike raw
  `WasapiLoopbackCapture`, source mode keeps producing aligned output during
  render gaps because the DMO owns both clocks. Your pull loop still gets frames;
  it just sees `S_FALSE` / 0-length when no new audio is ready, so keep the
  10 ms poll.
- **16 kHz mono only (for our use).** AEC is single-channel: the reference must
  be mono and the supported rates are 8000/11025/16000/22050. 16 kHz mono is the
  sweet spot for Whisper, so set exactly that output type; do not request stereo
  or 44.1/48 kHz from this DMO.
- **Stopping / flushing cleanly.** Stop the pull loop (`_running = false` and
  `Join`), then call `IMediaObject.Flush()` if you want to discard buffered
  audio, `FreeStreamingResources()`, and finally `Marshal.ReleaseComObject` on
  the property-store, media-object, and coclass references (release the interface
  views before the coclass). Dispose `MediaBuffer` / `DmoOutputDataBuffer`
  (unmanaged memory). Do all of this on the same MTA thread that created the DMO
  where practical.
- **HRESULT discipline.** `ProcessOutput` returning `S_FALSE` (1) is success with
  no data, not an error. Only treat negative HRESULTs as failures; do not
  `ThrowExceptionForHR` on `S_FALSE`.

---

## 7. Best existing implementations / references

1. **vvvv/VL.Devices.Kinect — decompiled Microsoft.Kinect 1.8 C# driver** (the
   single most useful C# artifact: full source-mode flow, property keys, device
   packing, SetOutputType 16k/16/mono, ProcessOutput INCOMPLETE loop, MTA thread;
   only missing piece is the CLSID instantiation, which you supply):
   https://github.com/vvvv/VL.Devices.Kinect/blob/master/src/Microsoft.Kinect/KinectAudioStream.cs
   and https://github.com/vvvv/VL.Devices.Kinect/blob/master/src/Microsoft.Kinect/KinectAudioSource.cs
2. **Microsoft Windows-classic-samples — AecSDKDemo.cpp** (canonical C++ init
   order, the exact DEVICE_INDEXES packing formula, and the ProcessOutput /
   INCOMPLETE / S_FALSE loop):
   https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/multimedia/audio/aecmicarray/AecSDKDemo.cpp
3. **NAudio 2.x DmoResampler / ResamplerDmoStream** (the coclass-cast-to-each-
   interface pattern and the public buffer/media-type plumbing to reuse):
   https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/Dmo/ResamplerMediaObject.cs
   and https://github.com/naudio/NAudio/blob/release/2.x/NAudio.Wasapi/ResamplerDmoStream.cs

Supporting reference (interop shapes for CLSID, enum, PROPVARIANT):
smourier/DirectN — `MFConstants.cs`, `AEC_SYSTEM_MODE.cs`, `Manual/PropVariant.cs`,
`Manual/IPropertyStore.cs`: https://github.com/smourier/DirectN

---

## Microsoft Learn sources

- Voice Capture DSP overview: https://learn.microsoft.com/en-us/windows/win32/medfound/voicecapturedmo
- AEC_SYSTEM_MODE enum: https://learn.microsoft.com/en-us/windows/win32/api/wmcodecdsp/ne-wmcodecdsp-aec_system_mode
- SYSTEM_MODE property: https://learn.microsoft.com/en-us/windows/win32/medfound/mfpkey-wmaaecma-system-modeproperty
- DMO_SOURCE_MODE property: https://learn.microsoft.com/en-us/windows/win32/medfound/mfpkey-wmaaecma-dmo-source-modeproperty
- DEVICE_INDEXES property: https://learn.microsoft.com/en-us/windows/win32/medfound/mfpkey-wmaaecma-device-indexesproperty

## Confidence notes

- CLSID, IIDs, fmtid, and all pids were verified against the installed Windows 11
  SDK `wmcodecdsp.h` (10.0.26100.0) and cross-checked against multiple
  independent C#/C++ implementations. High confidence.
- AEC_SYSTEM_MODE values verified (note OPTIBEAM_ARRAY_AND_AEC = 4, not 3).
- DEVICE_INDEXES packing verified verbatim from the C++ sample and matched in C#.
- **MFPKEY_WMAAECMA_RETAIN_FORMAT could not be verified and is believed not to be
  a real key.** Do not use it.
- The C# sketch reflects real API shapes but is a skeleton; match exact NAudio
  `IMediaObject` marshalling and size buffers from `GetOutputSizeInfo` in
  production code.
