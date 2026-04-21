using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

/// <summary>
/// Persists and queries batch analysis performance metrics (vision analysis, EXIF backfill, etc.).
/// </summary>
/// <remarks>
/// AnalysisMetrics record timing, success/failure counts, and per-photo durations for bulk operations.
/// Used to populate the Metrics dashboard and generate performance summaries for vision analysis jobs.
/// </remarks>
public interface IAnalysisMetricRepository
{
    /// <summary>Records a new analysis metric for a batch operation.</summary>
    /// <param name="metric">The AnalysisMetric to persist (includes operation type, counts, duration, etc.).</param>
    Task AddAsync(AnalysisMetric metric);

    /// <summary>Gets all analysis metrics in the database, newest first.</summary>
    /// <returns>All AnalysisMetric records, ordered by most recent.</returns>
    Task<IReadOnlyList<AnalysisMetric>> GetAllAsync();

    /// <summary>Gets the most recent N analysis metrics.</summary>
    /// <param name="count">Maximum number of recent metrics to retrieve.</param>
    /// <returns>The N most recent AnalysisMetric records.</returns>
    Task<IReadOnlyList<AnalysisMetric>> GetRecentAsync(int count);

    /// <summary>Computes aggregate statistics across all analysis metrics.</summary>
    /// <remarks>Includes photo count, average/min/max durations, throughput, tag/description stats.</remarks>
    /// <returns>A BatchMetricsSummary with overall performance data, or null if no metrics exist.</returns>
    Task<BatchMetricsSummary?> GetSummaryAsync();

    /// <summary>Computes aggregate statistics for a specific set of photos (e.g., subset of a batch).</summary>
    /// <param name="photoIds">The MediaFile IDs to include in the summary.</param>
    /// <returns>A BatchMetricsSummary for the specified photos, or null if no matching metrics exist.</returns>
    Task<BatchMetricsSummary?> GetBatchSummaryAsync(IReadOnlyList<Guid> photoIds);
}

/// <summary>
/// Aggregated statistics from one or more analysis metrics (vision analysis, EXIF backfill, thumbnails, etc.).
/// Displayed in the Metrics dashboard to show performance and throughput.
/// </summary>
/// <param name="PhotoCount">Number of photos in the batch.</param>
/// <param name="AvgTotalMs">Average duration per photo (total milliseconds).</param>
/// <param name="FastestMs">Fastest single-photo duration (milliseconds).</param>
/// <param name="SlowestMs">Slowest single-photo duration (milliseconds).</param>
/// <param name="FastestFile">File name of the fastest-analyzed photo.</param>
/// <param name="SlowestFile">File name of the slowest-analyzed photo.</param>
/// <param name="TotalMs">Total duration for all photos in the batch (milliseconds).</param>
/// <param name="PhotosPerMinute">Throughput: photos analyzed per minute.</param>
/// <param name="AvgTagCount">Average number of AI-generated tags per photo.</param>
/// <param name="AvgDescriptionLength">Average character length of AI-generated descriptions.</param>
public record BatchMetricsSummary(
    int     PhotoCount,
    double  AvgTotalMs,
    long    FastestMs,
    long    SlowestMs,
    string  FastestFile,
    string  SlowestFile,
    long    TotalMs,
    double  PhotosPerMinute,
    double  AvgTagCount,
    double  AvgDescriptionLength
);
