using PhotoIQPro.AI;
using PhotoIQPro.AI.Engines;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Tagging;

/// <summary>
/// Applies CLIP-based tag suggestions to photos using pre-computed tag embeddings.
/// </summary>
/// <remarks>
/// CLIP (Contrastive Language-Image Pre-training) embeds images and natural language text into
/// the same vector space. This service:
/// 1. Encodes a photo as a 512D ONNX embedding (ViT-B/32 backbone)
/// 2. Compares it against pre-computed embeddings for ~500 scene/object/activity/color/event tags
/// 3. Returns tags where the cosine similarity exceeds a category-specific threshold
///
/// Category-specific confidence thresholds prevent false positives:
/// - Scene/Object/Style/Color: 0.22 — reliable CLIP recognitions
/// - Activity: 0.26 — higher bar to avoid ghost tags (e.g. "eating" from couch photos)
/// - Event/Occasion: 0.32 — unambiguous markers only (no seasonal clothing false-positives)
///
/// Special logic filters outdoor-only tags (rain, beach, ocean) when the Indoor/Outdoor
/// resolver determines the photo is indoors, preventing hallucinations (bathtub water → "rain").
///
/// Tags are capped at 10 per image to avoid noise. Initialization is atomic via SemaphoreSlim
/// to handle concurrent requests during startup.
/// </remarks>
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
    ///
    /// Two thresholds by category:
    /// - Scene/Object/Style/Color: 0.22 — CLIP reliably recognises these; low threshold is safe.
    /// - Activity: 0.26 — activities describe transient behaviours that share embedding
    ///   space with mundane scenes (people on a couch → eating; workers → dancing).
    ///   The higher bar requires stronger visual evidence before an activity tag fires.
    ///   This is the architectural fix for ghost tags — not removing individual words.
    /// - Event/Occasion: 0.32 — occasion tags (thanksgiving, christmas, halloween) require
    ///   UNAMBIGUOUS visual markers (whole roasted turkey, decorated tree, costumes).
    ///   Red/white clothing, warm indoor lighting, and winter scenes are not sufficient.
    ///   Raised from 0.30 after christmas false-positives on child portraits with seasonal
    ///   clothing and indoor bathtub shots.
    /// </summary>
    private const float ConfidenceThreshold         = 0.22f;
    private const float ActivityConfidenceThreshold = 0.26f;
    private const float OccasionConfidenceThreshold = 0.32f;

    /// <summary>
    /// Tags that can only exist outdoors. Suppressed when the indoor/outdoor resolver
    /// determines the photo is indoors (indoor score ≥ outdoor score).
    /// This prevents CLIP from tagging bathtub water as "rain", wet skin as "snow", etc.
    /// </summary>
    private static readonly HashSet<string> OutdoorOnlyTags =
    [
        "rain", "snow", "beach", "ocean", "lake", "river",
        "forest", "mountains", "park", "street", "city", "sunset"
    ];
    private const int   MaxTagsPerImage             = 10;

    /// <summary>
    /// Initializes a new instance of the ClipTaggingService.
    /// </summary>
    /// <param name="imageEncoder">CLIP image encoder (ViT-B/32 via ONNX Runtime).</param>
    /// <param name="textEncoder">CLIP text encoder for embedding tag vocabulary prompts.</param>
    /// <param name="modelsPath">Directory containing the CLIP tokenizer files (vocab.json, merges.txt).</param>
    public ClipTaggingService(ClipEngine imageEncoder, ClipTextEngine textEncoder, string modelsPath)
    {
        _imageEncoder = imageEncoder;
        _textEncoder = textEncoder;
        _modelsPath = modelsPath;
    }

    /// <summary>
    /// Gets a value indicating whether CLIP tagging is available and ready to use.
    /// </summary>
    /// <remarks>
    /// True only if both the image and text models have initialized successfully AND
    /// the tag vocabulary embeddings have been pre-computed.
    /// </remarks>
    public bool IsAvailable => _tagEmbeddings != null && _imageEncoder.IsInitialized;

    /// <summary>
    /// Initializes the CLIP tagging service by loading models and pre-computing tag embeddings.
    /// </summary>
    /// <remarks>
    /// Safe to call multiple times; subsequent calls are no-ops due to SemaphoreSlim gating.
    /// If any step fails (missing models, I/O error, etc.), tagging degrades gracefully to unavailable.
    /// The image encoder is required; text encoder and tokenizer are optional (silent failures).
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
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
        catch { return; }

        var entries = TagVocabulary.Entries;
        var tokens  = entries.Select(e => tokenizer.Encode(e.Prompt)).ToArray();

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

        var allScores = _tagEmbeddings!
            .Select(t => new TagPrediction(t.Label, t.Category, Dot(imageEmb, t.Embedding)))
            .ToList();

        return ApplyFilters(allScores);
    }

    /// <summary>
    /// Applies all suppression rules and confidence thresholds to a raw scored list.
    /// Extracted as an internal static method so the filtering logic can be unit-tested
    /// without requiring ONNX models to be present.
    ///
    /// Rules applied in order:
    /// 1. Indoor/outdoor mutual exclusion — suppress the lower-scoring side (pre-threshold).
    /// 2. Outdoor-only tag suppression when photo is determined to be indoors.
    /// 3. Per-category confidence threshold (base / Activity / Event).
    /// 4. Sort by confidence descending and cap at MaxTagsPerImage.
    /// 5. Working-context suppression — remove athletic tags when "working" is present.
    /// </summary>
    internal static IReadOnlyList<TagPrediction> ApplyFilters(IList<TagPrediction> allScores)
    {
        // Resolve indoor/outdoor using raw (pre-threshold) scores so the loser is
        // suppressed even when only one of them crosses the threshold.
        var indoorScore  = allScores.FirstOrDefault(p => p.Label == "indoor")?.Confidence  ?? 0f;
        var outdoorScore = allScores.FirstOrDefault(p => p.Label == "outdoor")?.Confidence ?? 0f;
        var isIndoor     = indoorScore >= outdoorScore;
        var suppressIndoorOutdoor = (indoorScore > 0 || outdoorScore > 0)
            ? (isIndoor ? "outdoor" : "indoor")
            : null;

        var results = allScores
            .Where(p => p.Label != suppressIndoorOutdoor)
            // Suppress outdoor-only tags (rain, snow, beach, ocean, …) when photo is indoors.
            // Prevents: bathtub water → "rain", wet skin → "snow", etc.
            .Where(p => !isIndoor || !OutdoorOnlyTags.Contains(p.Label))
            .Where(p => p.Confidence >= (p.Category == TagCategory.Activity
                ? ActivityConfidenceThreshold
                : p.Category == TagCategory.Event
                ? OccasionConfidenceThreshold
                : ConfidenceThreshold))
            .OrderByDescending(p => p.Confidence)
            .Take(MaxTagsPerImage)
            .ToList();

        // Work context suppresses athletic activity tags — CLIP body-posture matching
        // fires dancing/running/sports on crouching workers.
        if (results.Any(p => p.Label == "working"))
            results.RemoveAll(p => p.Label is "dancing" or "running" or "sports");

        return results;
    }

    public async Task<float[]?> GetImageEmbeddingAsync(string imagePath, CancellationToken ct = default)
    {
        if (!_initAttempted) await InitializeAsync(ct);
        if (!_imageEncoder.IsInitialized) return null;
        try { return await _imageEncoder.GetImageEmbeddingAsync(imagePath); }
        catch { return null; }
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
