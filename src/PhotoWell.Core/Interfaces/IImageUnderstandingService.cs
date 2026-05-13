namespace PhotoWell.Core.Interfaces;

public interface IImageUnderstandingService
{
    bool IsAvailable { get; }
    /// <summary>Prompt version baked into this service instance (e.g. "v10a"). Stored on analyzed photos so stale descriptions can be detected.</summary>
    string PromptVersion { get; }
    Task<ImageUnderstanding> AnalyzeImageAsync(string imagePath, CancellationToken ct = default);
}

/// <summary>
/// Result from LLaVA image analysis: a natural-language description and
/// a list of extracted tags.
/// </summary>
public record ImageUnderstanding(string Description, IReadOnlyList<string> Tags)
{
    public static readonly ImageUnderstanding Empty = new(string.Empty, []);
}
