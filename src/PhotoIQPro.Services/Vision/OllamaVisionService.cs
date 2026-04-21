using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Implements image understanding using any vision model running locally via Ollama.
/// Produces a natural-language description for each photo.
/// CLIP owns tags; this service owns AiDescription only.
/// Gracefully returns ImageUnderstanding.Empty if Ollama is not running.
/// </summary>
public sealed class OllamaVisionService : IImageUnderstandingService
{
    private readonly OllamaClient _client;
    private readonly string _model;
    private readonly SemaphoreSlim _availLock = new(1, 1);
    // Two volatile fields instead of bool? so IsAvailable can be read from any thread
    // without acquiring the semaphore. volatile guarantees write visibility across cores.
    private volatile bool _availableChecked;
    private volatile bool _availableValue;

    /// <summary>
    /// Initializes a new instance of the OllamaVisionService.
    /// </summary>
    /// <param name="client">The OllamaClient for communicating with the local Ollama server.</param>
    /// <param name="model">The vision model to use (default: "llama3.2-vision"). Must be a model running on the local Ollama instance.</param>
    public OllamaVisionService(OllamaClient client, string model = "llama3.2-vision")
    {
        _client = client;
        _model  = model;
    }

    /// <summary>
    /// Gets a value indicating whether Ollama is running and the vision model is available.
    /// </summary>
    /// <remarks>
    /// This is a cached check that is refreshed only if the previous check indicated unavailability.
    /// This allows the app to gracefully handle Ollama starting after PhotoIQ, or the user
    /// enabling analysis after initially skipping it.
    /// </remarks>
    public bool IsAvailable => _availableValue;
    /// <summary>Gets the prompt template version used for vision analysis (for model versioning).</summary>
    public string PromptVersion => AppSettings.CurrentPromptVersion;

    /// <summary>
    /// Analyzes an image using a vision model running in Ollama, producing a natural-language description.
    /// </summary>
    /// <remarks>
    /// If Ollama is not running, returns ImageUnderstanding.Empty immediately.
    /// If Ollama returns an empty or degenerate response (token loops, garbage), automatically
    /// retries up to 2 times before giving up. On success, returns the parsed description.
    /// Failures are logged but don't throw; the photo remains in VisionPending state for retry on next startup.
    /// </remarks>
    /// <param name="imagePath">Full path to the image file to analyze (JPEG, PNG, or other format readable by Ollama).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ImageUnderstanding with Description and Tags (from the model's response), or Empty if analysis failed or Ollama is unavailable.</returns>
    public async Task<ImageUnderstanding> AnalyzeImageAsync(string imagePath, CancellationToken ct = default)
    {
        AppLog.Vision($"AnalyzeImageAsync start — model={_model} path={imagePath}");

        if (!await EnsureAvailableAsync(ct))
        {
            AppLog.Vision("SKIP — Ollama not reachable");
            return ImageUnderstanding.Empty;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            AppLog.Vision($"Image loaded — {bytes.Length / 1024} KB, sending to Ollama...");

            var b64 = Convert.ToBase64String(bytes);

            var response = await _client.GenerateAsync(_model, AppSettings.VisionAnalysisPrompt, b64, ct);
            AppLog.Vision($"Ollama response ({response.Length} chars): {response[..Math.Min(200, response.Length)]}");

            var result = ParseResponse(response);

            // Up to 2 silent retries — degenerate outputs (loops, garbage tokens) are
            // transient; a second or third attempt on the same image usually succeeds.
            for (int attempt = 1; attempt <= 2 && result == ImageUnderstanding.Empty && !ct.IsCancellationRequested; attempt++)
            {
                AppLog.Vision($"Empty result — retrying (attempt {attempt + 1})...");
                response = await _client.GenerateAsync(_model, AppSettings.VisionAnalysisPrompt, b64, ct);
                result   = ParseResponse(response);
                AppLog.Vision(result == ImageUnderstanding.Empty
                    ? $"Attempt {attempt + 1} also returned empty"
                    : $"Attempt {attempt + 1} OK — {result.Description[..Math.Min(80, result.Description.Length)]}");
            }

            if (result != ImageUnderstanding.Empty)
                AppLog.Vision($"OK — description: {result.Description[..Math.Min(120, result.Description.Length)]}");
            else
                AppLog.Vision("All attempts returned empty — leaving VisionPending for next startup");

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Vision($"EXCEPTION — {ex.GetType().Name}: {ex.Message}");
            return ImageUnderstanding.Empty;
        }
    }

    private async Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        if (_availableChecked) return _availableValue;

        await _availLock.WaitAsync(ct);
        try
        {
            if (!_availableChecked)
            {
                _availableValue = await _client.IsAvailableAsync(ct);
                // Only cache success. A failed check leaves _availableChecked = false so the
                // next call retries — handles Ollama starting after the app, or the user
                // clicking Re-analyze after Ollama was initially unreachable.
                if (_availableValue)
                    _availableChecked = true;
            }
        }
        finally { _availLock.Release(); }

        return _availableValue;
    }

    private static ImageUnderstanding ParseResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return ImageUnderstanding.Empty;

        // Collapse non-ASCII whitespace (U+00A0 non-breaking space, U+FFFD replacement char,
        // and other garbage tokens the model emits when degenerating) into regular spaces so
        // the word-split and n-gram checks in IsRepetitionLoop see clean token boundaries.
        response = System.Text.RegularExpressions.Regex.Replace(response, @"[^\x00-\x7F\r\n]", " ");
        response = System.Text.RegularExpressions.Regex.Replace(response, @" {2,}", " ").Trim();

        if (string.IsNullOrWhiteSpace(response)) return ImageUnderstanding.Empty;

        // Strip filler openers before the loop check so degenerate filler-prefixed responses
        // don't accidentally pass (e.g. "The image shows … … …" would otherwise be seen as
        // a valid short response by the word-count guard).
        response = TextNormalizer.StripFillerOpener(response);

        // Check the raw response for loops before trimming — the trimmer can produce a
        // clean-looking single sentence from a response that degenerates into "... ... ..."
        // in the tail, making the post-trim loop check blind to the actual degeneration.
        if (TextNormalizer.IsRepetitionLoop(response)) return ImageUnderstanding.Empty;

        // Flatten newlines — the model frequently emits a 2-paragraph structure
        // ("A subject in X\nShe is...") regardless of prompt instructions. Insert a
        // period when the merge point lacks terminal punctuation so the output reads
        // as proper prose rather than a run-on sentence.
        //
        // Ellipsis-paragraph fix: "A girl in a red t-shirt...\nShe is sitting" → "A girl in a red t-shirt. She is sitting"
        // The general newline regex only fires when the preceding char is NOT .!? so it misses
        // the "...\n" case. Strip trailing ellipses before merging paragraphs.
        var normalized = TextNormalizer.FlattenNewlines(response.Trim());
        var trimmed = TextNormalizer.TrimToCompleteSentences(normalized, 3);

        if (string.IsNullOrWhiteSpace(trimmed)) return ImageUnderstanding.Empty;

        // Second loop guard on the trimmed result — catches loops that start within the
        // first 3 sentences (rare, but possible with short responses).
        if (TextNormalizer.IsRepetitionLoop(trimmed)) return ImageUnderstanding.Empty;

        // Minimum content gate — single vague sentences ("A person is shown.") are not useful.
        // Treat them as failures so the retry has a chance to produce something better.
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 15) return ImageUnderstanding.Empty;

        // Apply gender-neutral post-processing: first NeutraliseGender (primary pass),
        // then EnforceGenderNeutrality (catch edge cases and additional gendered terms).
        var neutralized = TextNormalizer.NeutraliseGender(trimmed);
        var enforced = TextNormalizer.EnforceGenderNeutrality(neutralized);
        return new ImageUnderstanding(enforced, []);
    }
}
