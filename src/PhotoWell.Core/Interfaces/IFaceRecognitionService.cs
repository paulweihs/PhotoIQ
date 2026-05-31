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

    /// <summary>
    /// Returns the best-matching named person for the given embedding (no threshold applied).
    /// Only matches against persons with a non-null name — unnamed clusters are excluded.
    /// Returns null when there are no named persons or the embedding is empty.
    /// </summary>
    Task<(Guid PersonId, string Name, float Confidence)?> GetTopNamedMatchAsync(byte[] embedding);

    /// <summary>
    /// Updates a named person's AverageEmbedding with a newly assigned face embedding and
    /// invalidates the cache. Call whenever the user manually links a face to a named person
    /// so that person becomes discoverable by GetTopNamedMatchAsync for future suggestions.
    /// Degrades silently — never throws.
    /// </summary>
    Task TrackManualAssignmentAsync(Guid personId, byte[] faceEmbedding);
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
