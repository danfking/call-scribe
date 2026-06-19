using System.Runtime.InteropServices;
using NAudio.Dmo;

namespace CallScribe.Audio;

// Hand-rolled COM interop for the Windows Voice Capture DSP (CWMAudioAEC).
//
// NAudio 2.3.0 ships the streaming helper types (DmoMediaType, MediaBuffer,
// DmoOutputDataBuffer, DmoProcessOutputFlags) as public, but its IMediaObject and
// IPropertyStore interfaces are internal, so we cannot cast our coclass to them.
// We declare our own copies here. The vtable layout must match exactly, so every
// method appears in its real slot order even though we only call a handful.
//
// Plain language, no fancy dashes, on purpose.

/// <summary>
/// Minimal IMediaObject. All 21 methods are declared in vtable order so the
/// runtime builds the correct call thunks. Only the methods we actually invoke
/// have real signatures: the rest use IntPtr/int placeholders, because they are
/// never called and must not reference NAudio's non-public types.
/// </summary>
[ComImport]
[Guid("D8AD0F58-5494-4102-97C5-EC798E59BCF4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaObjectAec
{
    // 1 GetStreamCount
    [PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);

    // 2 GetInputStreamInfo
    [PreserveSig] int GetInputStreamInfo(int inputStreamIndex, out int flags);

    // 3 GetOutputStreamInfo
    [PreserveSig] int GetOutputStreamInfo(int outputStreamIndex, out int flags);

    // 4 GetInputType (placeholder)
    [PreserveSig] int GetInputType(int inputStreamIndex, int typeIndex, IntPtr mediaType);

    // 5 GetOutputType (placeholder)
    [PreserveSig] int GetOutputType(int outputStreamIndex, int typeIndex, IntPtr mediaType);

    // 6 SetInputType (placeholder: never called in source mode)
    [PreserveSig] int SetInputType(int inputStreamIndex, IntPtr mediaType, int flags);

    // 7 SetOutputType
    [PreserveSig] int SetOutputType(int outputStreamIndex, ref DmoMediaType mediaType, int flags);

    // 8 GetInputCurrentType (placeholder)
    [PreserveSig] int GetInputCurrentType(int inputStreamIndex, IntPtr mediaType);

    // 9 GetOutputCurrentType (placeholder)
    [PreserveSig] int GetOutputCurrentType(int outputStreamIndex, IntPtr mediaType);

    // 10 GetInputSizeInfo (placeholder)
    [PreserveSig] int GetInputSizeInfo(int inputStreamIndex, out int size, out int maxLookahead, out int alignment);

    // 11 GetOutputSizeInfo
    [PreserveSig] int GetOutputSizeInfo(int outputStreamIndex, out int size, out int alignment);

    // 12 GetInputMaxLatency (placeholder)
    [PreserveSig] int GetInputMaxLatency(int inputStreamIndex, out long referenceTimeMaxLatency);

    // 13 SetInputMaxLatency (placeholder)
    [PreserveSig] int SetInputMaxLatency(int inputStreamIndex, long referenceTimeMaxLatency);

    // 14 Flush
    [PreserveSig] int Flush();

    // 15 Discontinuity (placeholder)
    [PreserveSig] int Discontinuity(int inputStreamIndex);

    // 16 AllocateStreamingResources
    [PreserveSig] int AllocateStreamingResources();

    // 17 FreeStreamingResources
    [PreserveSig] int FreeStreamingResources();

    // 18 GetInputStatus (placeholder)
    [PreserveSig] int GetInputStatus(int inputStreamIndex, out int flags);

    // 19 ProcessInput (placeholder: never called in source mode)
    [PreserveSig] int ProcessInput(int inputStreamIndex, IntPtr mediaBuffer, int flags, long timestamp, long timeLength);

    // 20 ProcessOutput
    [PreserveSig]
    int ProcessOutput(
        DmoProcessOutputFlags flags,
        int outputBufferCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] outputBuffers,
        out int statusReserved);

    // 21 Lock
    [PreserveSig] int Lock(bool acquireLock);
}

/// <summary>Minimal IPropertyStore: just what we need to set DMO config keys.</summary>
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStoreAec
{
    [PreserveSig] int GetCount(out int propCount);
    [PreserveSig] int GetAt(int property, out PropertyKeyAec key);
    [PreserveSig] int GetValue(ref PropertyKeyAec key, out PropVariantAec value);
    [PreserveSig] int SetValue(ref PropertyKeyAec key, ref PropVariantAec value);
    [PreserveSig] int Commit();
}

/// <summary>
/// The two msdmo.dll helpers NAudio uses internally to allocate and free a
/// DMO_MEDIA_TYPE's format block. NAudio's own CreateDmoMediaTypeForWaveFormat is
/// non-public, so we call the same Win32 exports directly. MoInitMediaType sizes
/// cbFormat and allocates pbFormat; MoFreeMediaType releases them again.
/// </summary>
internal static class DmoInterop
{
    [DllImport("msdmo.dll")]
    public static extern int MoInitMediaType(ref DmoMediaType mediaType, int formatBlockBytes);

    [DllImport("msdmo.dll")]
    public static extern int MoFreeMediaType(ref DmoMediaType mediaType);
}

/// <summary>The PROPERTYKEY pair: a format GUID plus a numeric property id.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKeyAec
{
    public Guid fmtid;
    public int pid;
}

/// <summary>
/// Minimal PROPVARIANT. The real union is 16 bytes; we only ever set VT_I4 and
/// VT_BOOL, so we lay out just the vt tag and the value slot at offset 8 and
/// leave the rest as padding the runtime zero-initialises.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PropVariantAec
{
    private const ushort VtI4 = 3;
    private const ushort VtBool = 11;
    private const short VariantTrue = -1;
    private const short VariantFalse = 0;

    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public int intValue;     // VT_I4
    [FieldOffset(8)] public short boolValue;  // VT_BOOL (-1 = TRUE)

    public static PropVariantAec FromInt32(int value) => new() { vt = VtI4, intValue = value };

    public static PropVariantAec FromBool(bool value) =>
        new() { vt = VtBool, boolValue = value ? VariantTrue : VariantFalse };
}
