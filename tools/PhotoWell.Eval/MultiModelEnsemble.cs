using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PhotoWell.Eval;

/// <summary>
/// Multi-model ensemble for vision description evaluation.
/// Tests the same prompt across multiple vision models and selects the best result.
/// </summary>
public sealed class MultiModelEnsemble : IDisposable
{
    private readonly OllamaRunner _ollama;
    private readonly List<string> _models;
    private bool _disposed;

    public MultiModelEnsemble(params string[] models)
    {
        _ollama = new OllamaRunner();
        _models = new List<string>(models ?? Array.Empty<string>());
    }

    /// <summary>
    /// Generate descriptions for a single image using multiple models.
    /// Returns the best description based on length and diversity heuristics.
    /// </summary>
    public async Task<EnsembleResult> DescribeAsync(string imagePath, string prompt, CancellationToken ct = default)
    {
        var descriptions = new Dictionary<string, string>();
        var scores = new Dictionary<string, double>();

        Console.WriteLine($"  Testing {_models.Count} models...");

        // Run all models in parallel
        var tasks = _models.Select(async model =>
        {
            try
            {
                var available = await _ollama.IsModelAvailableAsync(model);
                if (!available)
                {
                    Console.WriteLine($"    ⚠ {model}: not available");
                    return (model, description: (string?)null, score: 0.0);
                }

                var description = await _ollama.GenerateAsync(model, prompt, imagePath);
                if (string.IsNullOrWhiteSpace(description))
                {
                    Console.WriteLine($"    ✗ {model}: empty response");
                    return (model, description: (string?)null, score: 0.0);
                }

                // Simple scoring: prefer descriptions with reasonable length (not too short, not too long)
                // and that contain specific details (multiple sentences, varied words)
                double score = ComputeDescriptionScore(description);
                Console.WriteLine($"    ✓ {model}: {description.Length} chars, score {score:F2}");

                return (model, description: (string?)description, score);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ✗ {model}: error - {ex.Message}");
                return (model, description: (string?)null, score: 0.0);
            }
        });

        var results = await Task.WhenAll(tasks);

        // Store results
        foreach (var (model, desc, score) in results)
        {
            if (desc != null)
            {
                descriptions[model] = desc;
                scores[model] = score;
            }
        }

        if (descriptions.Count == 0)
            return new EnsembleResult(imagePath, null, null, new(), "no models available");

        // Select best description
        var best = descriptions.OrderByDescending(kv => scores[kv.Key]).First();

        return new EnsembleResult(
            imagePath,
            best.Key,
            best.Value,
            descriptions.OrderByDescending(kv => scores[kv.Key]).ToDictionary(kv => kv.Key, kv => kv.Value),
            null);
    }

    /// <summary>
    /// Score a description based on heuristics for quality.
    /// Higher score = better description.
    /// </summary>
    private double ComputeDescriptionScore(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return 0.0;

        // Normalize whitespace and count metrics
        var normalized = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();
        var sentences = normalized.Split('.').Where(s => !string.IsNullOrWhiteSpace(s)).Count();
        var words = normalized.Split(' ').Length;

        double score = 0.0;

        // Prefer 2-3 sentences (typical target)
        if (sentences >= 2 && sentences <= 3)
            score += 2.0;
        else if (sentences >= 1 && sentences <= 4)
            score += 1.0;

        // Prefer descriptions in reasonable length (50-300 words)
        if (words >= 50 && words <= 300)
            score += 1.5;
        else if (words >= 30 && words <= 400)
            score += 0.5;

        // Penalize for repetition (looping is bad)
        var triplets = ExtractNGrams(normalized, 3);
        var uniqueTriplets = new HashSet<string>(triplets);
        double repetitionRatio = uniqueTriplets.Count > 0 ? (double)triplets.Count / uniqueTriplets.Count : 1.0;
        score *= repetitionRatio; // Reduce score if repetitive

        // Penalize vague/generic descriptions
        if (IsGenericDescription(normalized))
            score *= 0.5;

        return Math.Max(score, 0.1); // Minimum score to avoid zero
    }

    /// <summary>
    /// Extract n-grams (word sequences) from text.
    /// </summary>
    private List<string> ExtractNGrams(string text, int n)
    {
        var words = text.Split(' ');
        var ngrams = new List<string>();

        for (int i = 0; i <= words.Length - n; i++)
            ngrams.Add(string.Join(" ", words[i..(i + n)]));

        return ngrams;
    }

    /// <summary>
    /// Check if description is too generic (only subject, no detail).
    /// </summary>
    private bool IsGenericDescription(string description)
    {
        var genericPatterns = new[]
        {
            "^A [a-z]+ [a-z]+[.]?$", // "A golden retriever."
            "^The [a-z]+ [a-z]+[.]?$", // "The image shows X"
            "^[A-Z][a-z]+ [a-z]+[.]?$", // "Image shows X"
        };

        return genericPatterns.Any(pattern =>
            System.Text.RegularExpressions.Regex.IsMatch(description, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ollama?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Result from multi-model ensemble evaluation.
/// </summary>
public sealed record EnsembleResult(
    string ImagePath,
    string? BestModel,
    string? BestDescription,
    Dictionary<string, string> AllDescriptions,
    string? Error);
