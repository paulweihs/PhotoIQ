namespace PhotoWell.Core.Models;

/// <summary>
/// Performance and result metrics for a single photo's AI analysis (vision analysis, EXIF extraction, etc.).
/// </summary>
/// <remarks>
/// AnalysisMetrics are recorded after each photo is processed during bulk operations (ReanalyzeAllAsync, etc.).
/// They include timing breakdowns (preprocessing, inference, total), output statistics (tags, description length),
/// and hardware info (GPU used). Used to populate the Metrics dashboard and diagnose performance issues.
/// </remarks>
public class AnalysisMetric
{
    /// <summary>Unique identifier for this metric record.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The ID of the photo that was analyzed.</summary>
    public Guid PhotoId { get; set; }

    /// <summary>Name of the model used for analysis (e.g. "llava-1.5", "clip-vit-large-patch14").</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Width of the image in pixels (for tracking if resizing affected performance).</summary>
    public int ImageWidth { get; set; }

    /// <summary>Height of the image in pixels (for tracking if resizing affected performance).</summary>
    public int ImageHeight { get; set; }

    /// <summary>File format of the original photo (jpg, dng, png, heic, etc.).</summary>
    public string FileFormat { get; set; } = "";

    /// <summary>Size of the original image file in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Milliseconds spent on image decoding, resizing, and normalization before inference.</summary>
    public long PreprocessingMs { get; set; }

    /// <summary>Milliseconds spent on the actual model inference (vision analysis, embedding generation, etc.).</summary>
    public long InferenceMs { get; set; }

    /// <summary>Total milliseconds for the entire analysis (PreprocessingMs + InferenceMs).</summary>
    public long TotalAnalysisMs { get; set; }

    /// <summary>Number of AI-generated tags applied to this photo.</summary>
    public int TagCount { get; set; }

    /// <summary>Character length of the AI-generated description for this photo.</summary>
    public int DescriptionLength { get; set; }

    /// <summary>True if GPU acceleration was used during inference, false if CPU-only.</summary>
    public bool GpuUsed { get; set; }

    /// <summary>UTC timestamp when this photo was analyzed.</summary>
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}
