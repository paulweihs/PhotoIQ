using Microsoft.Extensions.DependencyInjection;
using PhotoWell.AI;
using PhotoWell.AI.Engines;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Services.Search;

/// <summary>
/// Performs semantic (meaning-based) image search via natural language queries using CLIP.
/// </summary>
/// <remarks>
/// Users can type natural language queries (e.g., "sunset over mountains", "people laughing") to find
/// semantically similar photos in their library. The service encodes the text query into a CLIP embedding
/// and matches it against the CLIP embeddings stored for every photo in the library.
///
/// The search requires ClipTextEngine to be initialized with the CLIP text model. If initialization
/// fails or the model is unavailable, IsAvailable=false and SearchAsync returns an empty result.
/// </remarks>
public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly ClipTextEngine _textEngine;
    private readonly string _modelsPath;
    private readonly IServiceScopeFactory _scopeFactory;
    private ClipTokenizer? _tokenizer;
    private bool _initAttempted;

    /// <summary>True if the semantic search service is initialized and ready to search (both text engine and tokenizer available).</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True if the CLIP text model file is available on disk.</summary>
    public bool IsModelAvailable => _textEngine.IsModelAvailable;

    /// <summary>Initializes a new instance of the SemanticSearchService.</summary>
    /// <param name="textEngine">The ClipTextEngine for encoding text queries into embeddings.</param>
    /// <param name="modelsPath">Path to the models directory (used to construct the tokenizer vocabulary).</param>
    /// <param name="scopeFactory">Factory for creating DI scopes to access the media file repository.</param>
    public SemanticSearchService(ClipTextEngine textEngine, string modelsPath, IServiceScopeFactory scopeFactory)
    {
        _textEngine = textEngine;
        _modelsPath = modelsPath;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Initializes the text engine and tokenizer on first access (lazy initialization).</summary>
    /// <remarks>Sets IsAvailable based on success. Called automatically by SearchAsync.</remarks>
    private async Task EnsureInitializedAsync()
    {
        if (_initAttempted) return;
        _initAttempted = true;
        try
        {
            if (!_textEngine.IsInitialized)
                await _textEngine.InitializeAsync();
            if (!_textEngine.IsInitialized) return;
            _tokenizer = new ClipTokenizer(_modelsPath);
            IsAvailable = true;
        }
        catch { IsAvailable = false; }
    }

    /// <summary>
    /// Searches the library for photos semantically similar to a natural language query.
    /// </summary>
    /// <remarks>
    /// Encodes the query text into a CLIP embedding, then searches the library for photos
    /// with the most similar embeddings (cosine similarity). Returns empty if the service
    /// is unavailable or the query fails to encode.
    /// </remarks>
    /// <param name="query">A natural language query (e.g., "sunset mountains", "happy people").</param>
    /// <param name="topN">Maximum number of results to return (default 50).</param>
    /// <returns>Up to topN MediaFiles ranked by semantic similarity to the query, highest first.</returns>
    public async Task<IReadOnlyList<MediaFile>> SearchAsync(string query, int topN = 50)
    {
        await EnsureInitializedAsync();
        if (!IsAvailable || _tokenizer == null) return [];

        float[][] embeddings;
        try
        {
            var tokens = _tokenizer.Encode(query);
            embeddings = await _textEngine.GetTextEmbeddingsAsync([tokens]);
        }
        catch { return []; }

        if (embeddings.Length == 0) return [];

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var results = await repo.SearchByEmbeddingAsync(embeddings[0], topN: topN);
        return [..results];
    }

    /// <summary>
    /// Finds photos visually similar to a given photo using its stored CLIP embedding.
    /// </summary>
    /// <remarks>
    /// Uses the stored CLIP embedding bytes from the source photo to search for similar photos.
    /// This method does not require the text encoder to be initialized (embeddings are precomputed).
    /// Returns empty if the source photo has no CLIP embedding.
    /// </remarks>
    /// <param name="sourceMediaFileId">The ID of the photo to find similar matches for.</param>
    /// <param name="topN">Maximum number of results to return (default 25).</param>
    /// <returns>Up to topN MediaFiles ranked by visual similarity to the source photo, highest first.</returns>
    public async Task<IReadOnlyList<MediaFile>> FindSimilarAsync(Guid sourceMediaFileId, int topN = 25)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var results = await repo.FindSimilarAsync(sourceMediaFileId, topN: topN);
        return [..results];
    }
}
