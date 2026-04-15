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
    public const string CurrentPromptVersion = "v12b";

    /// <summary>
    /// Vision analysis prompt sent to the Ollama vision model.
    /// v12b (2026-04-11): Two hallucination fixes from n=100 diverse eval (hallucination 64%, wrong_count 18%).
    ///        #1 Color uncertainty — added rule to avoid specifying colors of clothing/hair/objects
    ///           at thumbnail resolution; use general terms or omit. Targets #1 hallucination cause (~25%).
    ///        #2 Mandatory gender-neutral language — always use person/people/child/adult, never
    ///           man/woman/boy/girl or gendered pronouns. Eliminates gender-based hallucinations and
    ///           aligns with product philosophy of not making assumptions about users' photos.
    /// v12a (2026-04-09): Filler opener fix. Replaced phrase-list ban ("This photo", "This image") with
    ///        single-word ban ("This") to catch all "This image captures/features/shows" variants.
    ///        Also added "The photo" to the ban list. n=100 eval showed filler_opener at 10% (gate ≤5%)
    ///        with num_predict=450; this targets the root cause.
    /// v12 (2026-04-08): Label/packaging hallucination fix from n=30 Claude Code eval (87% pass, avg 7.7/9).
    ///        Failures were Photos 11 (invented turkey+sign), 27 (invented condiment labels),
    ///        28 (invented toy sets on table) — all caused by model trying to read small-print
    ///        text on labels/packaging at thumbnail resolution and hallucinating brand names.
    ///        Changed text-reading rule: was "quote verbatim if clearly readable" (too broad);
    ///        now explicitly bans label/packaging/small-print identification and restricts
    ///        text-quoting to large legible text (signs, banners, clothing logos, displays).
    /// v10d (2026-04-07): Silhouette hallucination fix. Changed noun-phrase example from
    ///        "A woman in a red dress" to a non-silhouette example to reduce anchoring.
    ///        Added explicit rule: never identify a backlit/silhouette shape as a person
    ///        unless facial features or clear body proportions confirm it.
    /// v10c (2026-04-05): v10b + four fixes from n=100 Claude Code vision eval (61% pass, avg 6.5/9).
    ///        #1 Clothing hallucination (G1 64%) — omit uncertain clothing/accessory details.
    ///        #3 Phrase-level loop guard — "Never repeat the same phrase or clause" in prompt;
    ///           app-layer n-gram check added alongside existing word-frequency guard.
    ///        #4 Gender misidentification — use neutral terms when gender is ambiguous.
    ///        #5 Activity misread (rear-view/blur) — describe only what is clearly observable.
    /// v10b (2026-04-05): v10a + explicit person-count rule. n=100 Claude Code eval showed
    ///        wrong_count at 28% — model frequently missed secondary people in frame.
    /// v10a (2026-04-03): v10 + ellipsis/paragraph-break ban. n=30 gate run passed.
    /// Gates: filler_opener ≤5%, hallucination ≤50%, formatting ≤42%, missing_subject ≤25%,
    ///        activity_inversion ≤22%, wrong_count ≤15%.
    /// v11/v12 both failed (filler_opener 33%) in earlier gate runs — v10 complexity limit
    ///        remains in force; this v12 makes only the minimal targeted text-rule change.
    /// </summary>
    public const string VisionAnalysisPrompt =
        """
        Describe this photo in 2-3 sentences for a searchable personal photo library.
        Start with a noun phrase naming the main subject — e.g. "Two cyclists on a mountain trail", "Three children playing", "A golden retriever". Never begin with "This", "The image", "The photo", or "The main subject". Then describe what is happening or the most specific visual details. If there is room, end with the setting or background.
        Before writing, count every person visible in the frame. Include the total in your opening noun phrase (e.g. "Five people", "Two children and one adult"). Do not omit any person who occupies more than a small corner of the frame. Always use gender-neutral terms — "person", "people", "child", "children", "adult", "toddler". Never use "man", "woman", "boy", "girl", or gendered pronouns ("he", "she", "his", "her").
        Never assume a silhouette, backlit shape, or partially visible figure is a person unless facial features or clear human body proportions are visible. If you cannot confirm it is a person, describe it as a shape, shadow, or by what it actually is (e.g. "a tree silhouette," "a glowing outline").
        Write in prose only — no bullet points, lists, ellipses (...), or paragraph breaks. Never repeat the same phrase or clause. Write in present tense. Ignore any date or time stamps visible in the image.
        Describe only what you can clearly see. Do not speculate about clothing patterns, accessories, or actions that are ambiguous, partially obscured, or blurry — omit uncertain details rather than guessing. If subjects are viewed from behind or their action is unclear, describe only what is evident. Do not specify exact colors of clothing, hair, or small objects unless you are certain — thumbnail images make colors ambiguous; use general terms ("dark jacket", "light dress") or omit the color.
        Do not attempt to read or identify text on product labels, food packaging, toy boxes, condiment jars, or any small print — describe the object by category only (e.g. "a jar of sauce", "a box of cereal"). Only quote text that is large and clearly legible, such as text on signs, banners, clothing logos, scoreboards, or large displays.
        """;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(FacesPath);
    }
}
