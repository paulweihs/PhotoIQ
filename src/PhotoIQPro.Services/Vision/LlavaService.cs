using System.Text.RegularExpressions;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Implements image understanding using LLaVA running locally via Ollama.
/// Produces a natural-language description and tag list for each photo.
/// Gracefully returns ImageUnderstanding.Empty if Ollama is not running.
/// </summary>
public sealed class LlavaService : IImageUnderstandingService
{
    private readonly OllamaClient _client;
    private readonly string _model;
    private readonly SemaphoreSlim _availLock = new(1, 1);
    private bool? _available;

    // Structured prompt so the response is easy to parse deterministically.
    private const string AnalysisPrompt =
        "Analyze this photo. Respond in exactly this format and nothing else:\n" +
        "DESCRIPTION: [A 1-2 sentence description of what is in the photo]\n" +
        "TAGS: [comma-separated list of 5-10 relevant lowercase tags]";

    public LlavaService(OllamaClient client, string model = "llava")
    {
        _client = client;
        _model = model;
    }

    /// <summary>
    /// False until the first availability check succeeds.
    /// Starts Ollama before launching PhotoIQ to enable LLaVA analysis.
    /// </summary>
    public bool IsAvailable => _available == true;

    public async Task<ImageUnderstanding> AnalyzeImageAsync(string imagePath, CancellationToken ct = default)
    {
        if (!await EnsureAvailableAsync(ct)) return ImageUnderstanding.Empty;

        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            var b64 = Convert.ToBase64String(bytes);
            var response = await _client.GenerateAsync(_model, AnalysisPrompt, b64, ct);
            return ParseResponse(response);
        }
        catch { return ImageUnderstanding.Empty; }
    }

    private async Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        if (_available.HasValue) return _available.Value;

        await _availLock.WaitAsync(ct);
        try
        {
            if (!_available.HasValue)
                _available = await _client.IsAvailableAsync(ct);
        }
        finally { _availLock.Release(); }

        return _available.Value;
    }

    private static ImageUnderstanding ParseResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return ImageUnderstanding.Empty;

        var descMatch = Regex.Match(response, @"DESCRIPTION:\s*(.+?)(?=\nTAGS:|$)", RegexOptions.Singleline);
        var tagsMatch = Regex.Match(response, @"TAGS:\s*(.+)", RegexOptions.Singleline);

        // Fall back to using the whole response as description if the format wasn't followed.
        var description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : response.Trim();

        IReadOnlyList<string> tags = tagsMatch.Success
            ? tagsMatch.Groups[1].Value
                .Split(',')
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length is > 0 and <= 60)
                .ToList()
            : [];

        return new ImageUnderstanding(description, tags);
    }
}
