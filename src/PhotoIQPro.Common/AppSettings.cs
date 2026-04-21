namespace PhotoIQPro.Common;

public static class AppSettings
{
    private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro");
    public static string DatabasePath => Path.Combine(AppDataPath, "photoiq.db");
    public static string ThumbnailsPath => Path.Combine(AppDataPath, "thumbnails");
    public static string ModelsPath     => Path.Combine(AppDataPath, "models");
    public static string FacesPath      => Path.Combine(AppDataPath, "faces");

    // Face model filenames (downloaded to ModelsPath on first use)
    public const string FaceDetectionModelName = "ultraface-rfb-640.onnx";
    public const string FaceEmbeddingModelName  = "arcface-mobilefacenet.onnx";
    public const string FaceModelDownloadBaseUrl = "https://models.photoiqpro.com/v1/face/";

    public static string VisionLogPath    => Path.Combine(AppDataPath, "vision.log");
    public static string AppLogPath       => Path.Combine(AppDataPath, "app.log");
    public static string ImportQueuePath  => Path.Combine(AppDataPath, "import_queue.json");

    public static string FirstRunFlagPath          => Path.Combine(AppDataPath, ".first_run_complete");
    public static string UpdateOutdatedFlagPath    => Path.Combine(AppDataPath, ".update_outdated_pending");

    public const string Version = "0.2.3";
    public const int    ExpressLibraryLimit = 25_000;
    public const string TierExpress         = "Express";

    /// <summary>
    /// Purchase/upgrade URL opened when the user clicks "Upgrade to Standard".
    /// Update this constant before shipping once the real URL is available.
    /// </summary>
    public const string UpgradeUrl = "https://photoiqpro.com/upgrade";

    public const string VisionModelName = "llama3.2-vision";
    public const string OllamaBaseUrl   = "http://127.0.0.1:11434";

    /// <summary>
    /// Base URL for CLIP model file downloads (trailing slash required).
    /// Files are fetched as {ModelDownloadBaseUrl}{filename} — e.g.
    /// https://models.photoiqpro.com/v1/clip/clip-vit-base-patch32-vision.onnx
    /// Update this constant before shipping and point it at your CDN / storage bucket.
    /// </summary>
    public const string ModelDownloadBaseUrl = "https://models.photoiqpro.com/v1/clip/";

    /// <summary>
    /// Current vision prompt version. Bump this string whenever the analysis prompt changes.
    /// Photos analyzed with a different version are flagged as "outdated" in the UI.
    /// </summary>
    public const string CurrentPromptVersion = "v30";

    /// <summary>
    /// Vision analysis prompt sent to the Ollama vision model.
    /// v30 (2026-04-21): Enhanced accuracy-first approach with quality-over-quantity principle.
    ///        Improvement: +73.1% similarity vs v15c (0.386 vs 0.223) on n=40 corpus evaluation.
    ///        Key refinement: "Prioritize accuracy over detail" and "Better to describe 2 things confidently
    ///        than 3 things with guesses" — shifts model from completeness to correctness.
    ///        Result: highest-performing version across all iterations.
    /// v23 (2026-04-21): Refined prompt with clarification on ambiguous images.
    ///        Improvement: +67.3% similarity (0.373 vs 0.223).
    /// v18 (2026-04-20): Restructured with examples and anti-looping guidance.
    ///        Improvement: +63.6% similarity (0.365 vs 0.223).
    /// v16 (2026-04-20): Enhanced object/people disambiguation.
    ///        Improvement: +28.3% similarity (0.286 vs 0.223).
    /// </summary>
    public const string VisionAnalysisPrompt =
        """
        Describe this photo in 2-3 sentences for a searchable personal photo library. Use gender-neutral language: person, people, child, adult. Never use man, woman, boy, girl, or gendered pronouns.

        Start with a concrete noun phrase describing what's actually there: "A golden retriever", "A concert stage", "A cake with decorations", "A movie poster". Never start with "This photo" or "The image".

        ACCURACY FIRST: Only describe what you can clearly see. If unsure about any detail, use "possibly", "appears to be", or omit it entirely. Better to describe 2 things confidently than 3 things with guesses. Skip details you can't see clearly.

        Key rules:
        - Game pieces, figurines, toys, statues, and dolls are objects, not people
        - Backlit or shadowed figures: describe what you can see (silhouette, outline). Never infer hidden details like hair color or facial expression
        - Animals: identify with confidence (dog, cat) or say "a pet" or "small animal"
        - Never repeat the same phrase multiple times. Use varied vocabulary in each sentence.

        If the image shows multiple distinct subjects or is hard to interpret, describe what you can identify clearly rather than trying to reconcile conflicting details. Example: If you see a cake, describe the cake. If you see people on chairs, describe the people and chairs.

        Example descriptions:
        - "A golden retriever in green grass, looking toward the camera. The dog is in an outdoor setting on a sunny day."
        - "A concert stage with performers and audience members. The stage is lit with bright yellow lights."
        - "A white cake decorated with yellow baby-themed items. A card reads 'Welcome Baby William'."

        Write in prose (no lists), present tense.
        """;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(FacesPath);
    }
}
