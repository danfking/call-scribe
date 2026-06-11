namespace CallScribe.Audio;

/// <summary>A copied slice of captured audio. Buffers are copied out of NAudio's
/// reused capture buffer, so a chunk is safe to hold and hand to multiple consumers
/// (the WAV writer today, a live transcriber in future).</summary>
public readonly record struct AudioChunk(byte[] Buffer, int Count);
