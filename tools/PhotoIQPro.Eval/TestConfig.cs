namespace PhotoIQPro.Eval;

/// <summary>
/// A single photo test case with per-photo gate configuration.
/// </summary>
public record PhotoTestCase(
    string FileName,
    string[] Gate1Forbidden,
    string[] Gate2Forbidden,
    bool HasGesture,
    string[] Gate6ForbiddenMisclassifications
);

/// <summary>
/// All fixed configuration for the evaluation harness:
/// the prompt, per-photo test cases, and global gate word lists.
/// </summary>
public static class TestConfig
{
    /// <summary>
    /// The fixed vision prompt used for every model, every photo, every pass.
    /// Must not be altered between runs so comparisons are valid.
    /// </summary>
    // v7 — synced with OllamaVisionService.AnalysisPrompt (2026-03-30).
    // v6→v7: subject-agnostic rewrite, 2-3 sentence budget, positive framing.
    // Intentionally breaks comparability with v6 runs — new eval series baseline.
    // Do not alter between runs of the same eval series — breaks comparability.
    public const string Prompt =
        """
        Describe this photo in 2-3 sentences for a searchable personal photo library.
        Identify the main subject, describe what is happening or the key visual details, and note the setting.
        Be specific: name the type of subject, what they or it looks like, what is occurring, and where. Write in present tense.
        Only describe what is clearly visible. Do not infer relationships, emotions, or events not shown.
        """;

    /// <summary>
    /// Gate 3 — global filler/context words that must not appear without visual evidence.
    /// </summary>
    public static readonly string[] FillerTags =
    [
        "cooking", "travel", "wedding", "birthday", "holiday", "dancing"
    ];

    /// <summary>
    /// Gate 4 — relationship inference words that must not appear in any description.
    /// "seated" is also included because it infers posture not clearly visible.
    /// </summary>
    public static readonly string[] RelationshipTerms =
    [
        "family", "couple", "romantic", "arm around",
        "smiling at each other", "friends", "friendship", "seated"
    ];

    /// <summary>
    /// Default Ollama model candidates to evaluate when none are specified on the command line.
    /// </summary>
    public static readonly string[] DefaultModels =
    [
        "llama3.2-vision", "minicpm-v", "moondream", "llava:13b", "bakllava"
    ];

    /// <summary>
    /// The fixed photo test cases. Each entry defines the file and its per-gate word lists.
    /// </summary>
    public static readonly PhotoTestCase[] Photos =
    [
        new PhotoTestCase(
            FileName: "DSCF0014.JPG",
            Gate1Forbidden: [],
            Gate2Forbidden: [],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0016.JPG",
            Gate1Forbidden: ["ladder", "drill", "brush", "screwdriver", "toolbox"],
            Gate2Forbidden: ["cleaning", "dusting", "washing"],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0017.JPG",
            Gate1Forbidden: ["ladder", "drill", "screwdriver", "toolbox", "outlet", "wire", "stair"],
            Gate2Forbidden: ["drilling", "attaching wire", "wires"],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0018.JPG",
            Gate1Forbidden: ["table", "wine glass", "wine glasses"],
            Gate2Forbidden: [],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0019.JPG",
            Gate1Forbidden: ["table", "wine glass"],
            Gate2Forbidden: [],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0021.JPG",
            Gate1Forbidden: ["wine glass"],
            Gate2Forbidden: [],
            HasGesture: true,
            Gate6ForbiddenMisclassifications: ["obscene", "rude", "inappropriate", "vulgar", "offensive"]
        ),
        new PhotoTestCase(
            FileName: "DSCF0020.JPG",
            Gate1Forbidden: [],
            Gate2Forbidden: [],
            HasGesture: false,
            Gate6ForbiddenMisclassifications: []
        ),
        new PhotoTestCase(
            FileName: "DSCF0022.JPG",
            Gate1Forbidden: [],
            Gate2Forbidden: [],
            HasGesture: true,
            Gate6ForbiddenMisclassifications: ["obscene", "violent", "attack", "fighting", "aggressive"]
        ),
    ];
}
