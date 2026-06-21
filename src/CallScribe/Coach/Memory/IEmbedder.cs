namespace CallScribe.Coach.Memory;

/// <summary>Whether text is being embedded for storage or as a search query. Some models
/// (e.g. nomic-embed-text) need different task prefixes for each, or retrieval quality
/// collapses.</summary>
public enum EmbedPurpose { Document, Query }

/// <summary>Turns text into a fixed-length embedding vector for semantic recall.</summary>
public interface IEmbedder
{
    /// <summary>Vector length the model produces; must match the memories column.</summary>
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, EmbedPurpose purpose, CancellationToken ct);
}
