using PhotoIQPro.AI;
using PhotoIQPro.AI.Engines;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Tagging;

public sealed class ClipTaggingService : ITaggingService
{
    private readonly ClipEngine _imageEncoder;
    private readonly ClipTextEngine _textEncoder;
    private readonly string _modelsPath;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initAttempted;

    // Populated once during initialization; null means tagging is unavailable.
    private (float[] Embedding, string Label, TagCategory Category)[]? _tagEmbeddings;

    /// <summary>
    /// Minimum cosine similarity (image·text) required to attach a tag.
    /// CLIP ViT-B/32 similarity scores for true matches typically fall in [0.20, 0.35].
    /// </summary>
    private const float ConfidenceThreshold = 0.22f;
    private const int MaxTagsPerImage = 10;

    public ClipTaggingService(ClipEngine imageEncoder, ClipTextEngine textEncoder, string modelsPath)
    {
        _imageEncoder = imageEncoder;
        _textEncoder = textEncoder;
        _modelsPath = modelsPath;
    }

    // True only after a successful init with both models + tokenizer files present.
    public bool IsAvailable => _tagEmbeddings != null && _imageEncoder.IsInitialized;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initAttempted) return;
            _initAttempted = true;
            await InitializeCoreAsync(ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Vision model is required; let its own exception surface if missing.
        try { await _imageEncoder.InitializeAsync(); }
        catch { return; }

        // Text model + tokenizer files are optional — degrade silently when absent.
        if (!_textEncoder.IsModelAvailable) return;

        try { await _textEncoder.InitializeAsync(); }
        catch { return; }

        ClipTokenizer tokenizer;
        try { tokenizer = new ClipTokenizer(_modelsPath); }
        catch { return; }  // vocab.json / merges.txt not found

        var entries = TagVocabulary.Entries;
        var tokens = entries.Select(e => tokenizer.Encode(e.Prompt)).ToArray();

        float[][] embeddings;
        try { embeddings = await _textEncoder.GetTextEmbeddingsAsync(tokens); }
        catch { return; }

        if (embeddings.Length != entries.Count) return;

        _tagEmbeddings = entries
            .Zip(embeddings, (e, emb) => (emb, e.Label, e.Category))
            .ToArray();
    }

    public async Task<IReadOnlyList<TagPrediction>> GenerateTagsAsync(string imagePath, CancellationToken ct = default)
    {
        if (!_initAttempted)
            await InitializeAsync(ct);

        if (!IsAvailable) return [];

        float[] imageEmb;
        try { imageEmb = await _imageEncoder.GetImageEmbeddingAsync(imagePath); }
        catch { return []; }

        return _tagEmbeddings!
            .Select(t => new TagPrediction(t.Label, t.Category, Dot(imageEmb, t.Embedding)))
            .Where(p => p.Confidence >= ConfidenceThreshold)
            .OrderByDescending(p => p.Confidence)
            .Take(MaxTagsPerImage)
            .ToList();
    }

    // Both vectors are L2-normalised, so dot product == cosine similarity.
    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++) sum += a[i] * b[i];
        return sum;
    }
}
