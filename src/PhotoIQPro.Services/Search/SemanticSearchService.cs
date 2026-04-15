using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.AI;
using PhotoIQPro.AI.Engines;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Search;

public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly ClipTextEngine _textEngine;
    private readonly string _modelsPath;
    private readonly IServiceScopeFactory _scopeFactory;
    private ClipTokenizer? _tokenizer;
    private bool _initAttempted;

    public bool IsAvailable { get; private set; }
    public bool IsModelAvailable => _textEngine.IsModelAvailable;

    public SemanticSearchService(ClipTextEngine textEngine, string modelsPath, IServiceScopeFactory scopeFactory)
    {
        _textEngine   = textEngine;
        _modelsPath   = modelsPath;
        _scopeFactory = scopeFactory;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initAttempted) return;
        _initAttempted = true;
        try
        {
            if (!_textEngine.IsInitialized)
                await _textEngine.InitializeAsync();
            if (!_textEngine.IsInitialized) return;
            _tokenizer  = new ClipTokenizer(_modelsPath);
            IsAvailable = true;
        }
        catch { IsAvailable = false; }
    }

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
        var repo        = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var results     = await repo.SearchByEmbeddingAsync(embeddings[0], topN: topN);
        return [..results];
    }
}
