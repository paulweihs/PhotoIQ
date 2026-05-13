using PhotoWell.Core.Models;

namespace PhotoWell.Core.Interfaces;

public interface IFaceRecognitionService
{
    /// <summary>
    /// Matches a newly saved Face against known persons using cosine similarity.
    /// Creates a new unnamed Person cluster when no match exceeds the low-confidence threshold.
    /// Updates the matched Person's AverageEmbedding incrementally.
    /// Degrades silently — never throws.
    /// </summary>
    Task IdentifyFaceAsync(Face face, CancellationToken ct = default);

    /// <summary>
    /// Invalidates the in-memory embedding cache so the next call to IdentifyFaceAsync
    /// reloads from the DB. Call after a user names or merges a person.
    /// </summary>
    Task InvalidateCacheAsync();
}

/// <summary>Classification of how confidently a face was matched to a person.</summary>
public enum MatchConfidence
{
    /// <summary>Similarity ≥ HIGH_THRESHOLD — auto-linked, no confirmation needed.</summary>
    High,
    /// <summary>Similarity between LOW and HIGH threshold — linked, but Phase 3 will ask for confirmation.</summary>
    Low,
    /// <summary>No match found — a new unnamed Person cluster was created.</summary>
    NewCluster,
}
