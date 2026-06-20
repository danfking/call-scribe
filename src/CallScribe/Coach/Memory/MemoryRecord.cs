namespace CallScribe.Coach.Memory;

/// <summary>Kinds of durable memory extracted from a meeting for future recall.</summary>
public enum MemoryKind { Insight, Decision, ActionItem, PersonFact, Preference }

/// <summary>A memory returned by semantic recall, with its cosine distance to the query
/// (smaller = closer).</summary>
public readonly record struct RecalledMemory(MemoryKind Kind, string Text, double Distance);

/// <summary>One persisted transcript line, read back for end-of-meeting consolidation.
/// Named to avoid colliding with Transcription.TranscriptSegment (the batch model).</summary>
public readonly record struct TranscriptLine(DateTime At, string Speaker, string Text);
