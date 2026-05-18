namespace PhotoWell.Common;

public static class AppSettings
{
    private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWell");
    public static string DatabasePath => Path.Combine(AppDataPath, "photowell.db");
    public static string ThumbnailsPath => Path.Combine(AppDataPath, "thumbnails");
    public static string ModelsPath     => Path.Combine(AppDataPath, "models");
    public static string FacesPath      => Path.Combine(AppDataPath, "faces");

    // Face model filenames (downloaded to ModelsPath on first use)
    public const string FaceDetectionModelName = "ultraface-rfb-640.onnx";
    public const string FaceEmbeddingModelName  = "arcface-mobilefacenet.onnx";

    // HuggingFace direct download URLs for face detection models
    // UltraFace from onnxmodelzoo: https://huggingface.co/onnxmodelzoo/version-RFB-640
    // ArcFace from garavv: https://huggingface.co/garavv/arcface-onnx
    public const string FaceDetectionModelUrl = "https://huggingface.co/onnxmodelzoo/version-RFB-640/resolve/main/version-RFB-640.onnx";
    public const string FaceEmbeddingModelUrl = "https://huggingface.co/garavv/arcface-onnx/resolve/main/arc.onnx";

    public static string VisionLogPath    => Path.Combine(AppDataPath, "vision.log");
    public static string AppLogPath       => Path.Combine(AppDataPath, "app.log");
    public static string ImportQueuePath  => Path.Combine(AppDataPath, "import_queue.json");

    public static string DuplicateIgnoredFoldersPath     => Path.Combine(AppDataPath, "duplicate_ignored_folders.txt");
    public static string FirstRunFlagPath               => Path.Combine(AppDataPath, ".first_run_complete");
    public static string UpdateOutdatedFlagPath         => Path.Combine(AppDataPath, ".update_outdated_pending");
    public static string ThumbnailRegenCheckpointPath   => Path.Combine(AppDataPath, ".thumbnail_regen_checkpoint");

    public const string Version = "0.2.3";
    public const int    ExpressLibraryLimit = 25_000;
    public const string TierExpress         = "Express";

    /// <summary>
    /// Purchase/upgrade URL opened when the user clicks "Upgrade to Standard".
    /// Update this constant before shipping once the real URL is available.
    /// </summary>
    public const string UpgradeUrl = "https://photowell.app/upgrade";

    /// <summary>
    /// The base Ollama model pulled from the registry.
    /// Used by OllamaSetupService to ensure the model is downloaded before creating the derived variant.
    /// </summary>
    public const string VisionBaseModelName = "minicpm-v";

    /// <summary>
    /// The derived Ollama model used for all inference. Created once from VisionBaseModelName with
    /// num_ctx=1024 baked into the Modelfile so Ollama always loads it at that context size,
    /// keeping the KV cache within VRAM and achieving 5–7 s/photo (vs 47–125 s when loaded at 2048).
    /// </summary>
    public const string VisionModelName = "photowell-minicpm-v";

    public const string OllamaBaseUrl   = "http://127.0.0.1:11434";

    /// <summary>
    /// Base URL for CLIP model file downloads (trailing slash required).
    /// Files are fetched as {ModelDownloadBaseUrl}{filename} — e.g.
    /// https://models.photowell.app/v1/clip/clip-vit-base-patch32-vision.onnx
    /// Update this constant before shipping and point it at your CDN / storage bucket.
    /// </summary>
    public const string ModelDownloadBaseUrl = "https://models.photowell.app/v1/clip/";

    /// <summary>
    /// Current vision prompt version. Bump this string whenever the analysis prompt changes.
    /// Photos analyzed with a different version are flagged as "outdated" in the UI.
    /// </summary>
    public const string CurrentPromptVersion = "v45-minicpm";

    /// <summary>
    /// Current post-processing pipeline version. Bump this string whenever TextNormalizer
    /// logic changes in a way that would produce meaningfully different output for existing
    /// descriptions (e.g. new filler stripping, gender normalisation changes, verb agreement).
    /// Photos whose stored PostProcessVersion differs from this are flagged as "outdated".
    /// pp1 (2026-05-18): Initial versioning. Includes filler-opener stripping, likely-gender
    ///     normalisation, double-comma collapse, subject-verb agreement fixes.
    /// </summary>
    public const string CurrentPostProcessVersion = "pp1";

    /// <summary>
    /// Vision analysis prompt sent to the Ollama vision model.
    /// v45-minicpm (2026-05-12): Removed gender-neutral rule from prompt — it was preventing
    ///        the v43 "who appears to be a X" post-processing hint from ever firing, because the
    ///        model followed instructions and never said "woman"/"man"/etc., leaving the
    ///        TextNormalizer nothing to replace. Model now uses natural gendered terms; post-
    ///        processing converts them to "person, who appears to be a woman," etc. Updated
    ///        sentence (1) examples to include gendered terms so the model follows the pattern.
    ///        Strengthened TEXT RULE to explicitly cover clothing labels/logos.
    /// v44-minicpm (2026-05-11): Added TEXT RULE — only include brand names/labels/signs
    ///        that can be read with certainty; if unsure describe the object type instead.
    ///        Addresses field-observed hallucination of brand names: model was inventing
    ///        "Corona"/"Eskimo Creek" for bottles labelled "ERIN CHRIS"/"LORINA".
    ///        The existing "do not guess" rule was not specific enough for text/labels.
    /// v43-minicpm (2026-05-11): Post-processing updated — gendered nouns (woman/man/girl/boy,
    ///        singular and plural) now replaced with neutral noun + "who appears to be a X" hint
    ///        rather than plain neutral substitution. Preserves searchability of gendered terms
    ///        while adding epistemic hedging. Possessives (man's/girl's etc.) stay plain neutral.
    ///        Plural agreement handled separately ("who appear to be women" vs "who appears to be a woman").
    /// v42-minicpm (2026-05-03): Removed "Sentence N:" label format from instructions — models
    ///        echo these labels literally into output, polluting descriptions and search.
    ///        Replaced with numbered parenthetical format (1)/(2)/(3) which models treat as
    ///        structural hints only. Added explicit "Do not start sentences with Sentence 1..." rule.
    ///        Stripped "dominant subject" phrase from instructions (was appearing in output).
    ///        TextNormalizer updated to strip any surviving "Sentence N:" labels as a safety net.
    /// v35-minicpm (2026-04-27): Addresses four field-reported failure modes from v34-minicpm:
    ///        (1) PEOPLE FIRST rule — people in the foreground are always the primary subject even
    ///            when a large background element (Christmas tree, banner, building) dominates area.
    ///        (2) Deliberate counting step — "scan left to right, top to bottom" before writing
    ///            reduces undercounting in group photos and single-person misses in two-person shots.
    ///        (3) Named recognizable figures — Easter Bunny, Santa Claus, mascots, etc. are now
    ///            explicitly named in the description so they appear in text search.
    ///        (4) Longer descriptions — increased from 2 sentences to 3–4 sentences; TrimToCompleteSentences
    ///            raised from 3 to 5. Allows the model to cover setting AND background details.
    /// v34-minicpm (2026-04-25): Overall winner — 62% pass rate, 8.7/11 avg (n=100).
    ///        Model changed to minicpm-v (VisionModelName). minicpm-v has a significantly better
    ///        vision encoder than llama3.2-vision for real-world personal photos.
    ///        Key addition over v34: "Identify the single most prominent subject that fills the
    ///        largest area of the frame" anchor sentence, which forces the model to ground on the
    ///        dominant visual element rather than free-associating from patches. Eliminates most
    ///        wrong-scene hallucinations. qwen2.5vl:7b also tested (45% pass) — solid fallback.
    ///        Model comparison on v34 prompt: llama3.2-vision 29%, qwen2.5vl 45%, minicpm-v 62%.
    /// v34 (2026-04-25): Winner of harness eval run (n=100, 7.1/11 avg, 29% pass vs 24% for v32).
    ///        Structural rewrite targeting the two biggest failure modes found in v32:
    ///        (1) Anti-looping: added "exactly 2 sentences, stop after sentence 2" to fix G7
    ///            (ProperLength) which was failing on ~70% of images due to 250+ word looping outputs.
    ///        (2) Structured sentence roles: Sentence 1 = main subject noun phrase, Sentence 2 =
    ///            setting (indoor/outdoor + specific location) + key details. Fixed G4 (SettingMatch).
    ///        Also removed the example descriptions, which were anchoring the model toward
    ///        outdoor/animal scenes and causing wrong descriptions on unrelated photos.
    ///        Strengthened PEOPLE COUNT instruction: "state the exact number you can count".
    /// v33 (2026-04-25): Intermediate — added anti-looping and 40-80 word limit. Slightly worse
    ///        than v34 (26% pass) because structured sentence roles were not yet added.
    /// v32 (2026-04-25): Improved multi-person descriptions. Added MULTIPLE PEOPLE rule directing
    ///        the model to distinguish people by position/clothing rather than gender, eliminating
    ///        "the person... the person" ambiguity. Added example showing two-person distinction.
    ///        Also added crowd guidance: describe group context first, enumerate individuals only
    ///        when a specific detail is worth calling out.
    /// v31 (2026-04-23): Added explicit anti-hallucination rule for people. Field reports showed
    ///        the model hallucinating people in landscape/plant-only photos. Added: "PEOPLE RULE:
    ///        Only mention a person or people if you can clearly see a human body or face. Plants,
    ///        flowers, branches, and shadows are never people." Also pre-resized images to 1120px
    ///        before sending (see OllamaVisionService.ResizeForOllamaAsync) to prevent Ollama's
    ///        internal downsampling of large raw files from causing tile-based hallucinations.
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
        Describe this photo in exactly 3 sentences for a searchable personal photo library. Stop after 3 sentences.

        (1) Name the main subject with a concrete noun phrase. If people are present and visible in the foreground, they are always the main subject — state the exact count and apparent gender (e.g. "One woman smiling at the camera", "Two men sitting at a restaurant table", "One girl playing in a backyard"). For non-people subjects use a specific noun phrase (e.g. "A tiered birthday cake", "A small dog resting on an armchair").
        (2) Describe 1-3 key visible details — colors, clothing, objects held or nearby, expressions, or distinguishing features. Only describe what you can clearly see.
        (3) State where the photo was taken: indoor or outdoor, and the specific location type (kitchen, park, beach, school gym, restaurant, living room, etc.).

        Rules:
        - Exact people count: say "one woman", "two men", "three girls" — never "a person" or "some people". For uncountable crowds say "a large group of people".
        - Named figures: if you see Easter Bunny, Santa Claus, or a sports mascot, name them.
        - Only describe what is clearly visible. Do not guess or invent details.
        - TEXT RULE: Only include text (brand names, labels, clothing logos, signs) if you can read every word with certainty. If unsure, describe the object instead (e.g. "a pink top" not a guessed brand name, "a bottle of amber liquid" not a guessed label). Never invent text you cannot clearly read.
        - Do not mention that this is a photo or image. Do not start sentences with "Sentence 1", "Sentence 2", etc.
        """;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(FacesPath);
    }
}
