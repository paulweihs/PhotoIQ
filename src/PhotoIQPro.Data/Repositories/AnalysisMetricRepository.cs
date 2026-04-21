using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

/// <summary>
/// Repository for AnalysisMetric persistence and performance tracking queries.
/// </summary>
/// <remarks>
/// AnalysisMetrics track the performance of CLIP and LLaVA inference on individual photos:
/// - Execution time for tagging, vision analysis, and face detection
/// - Tag and description output sizes
/// - Identified as fastest/slowest in a batch for user feedback
///
/// Metrics are used to populate the Metrics dashboard (average processing time, throughput,
/// quality metrics) and help diagnose performance issues.
/// </remarks>
public class AnalysisMetricRepository : IAnalysisMetricRepository
{
    private readonly PhotoIQContext _context;
    /// <summary>Initializes a new instance of the AnalysisMetricRepository.</summary>
    /// <param name="context">The PhotoIQContext for database access.</param>
    public AnalysisMetricRepository(PhotoIQContext context) => _context = context;

    /// <summary>
    /// Adds a new performance metric for a single photo analysis.
    /// </summary>
    /// <remarks>
    /// Called after CLIP and LLaVA analysis completes to record execution times, output sizes, and quality metrics.
    /// </remarks>
    /// <param name="metric">The AnalysisMetric to store.</param>
    public async Task AddAsync(AnalysisMetric metric)
    {
        await _context.AnalysisMetrics.AddAsync(metric);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets all performance metrics in the database, ordered by most recent first.
    /// </summary>
    /// <returns>All AnalysisMetric records, sorted by AnalyzedAt descending.</returns>
    public async Task<IReadOnlyList<AnalysisMetric>> GetAllAsync() =>
        await _context.AnalysisMetrics.OrderByDescending(m => m.AnalyzedAt).ToListAsync();

    /// <summary>
    /// Gets the N most recent performance metrics.
    /// </summary>
    /// <remarks>Useful for computing recent average performance (last 100 photos, last hour, etc.).</remarks>
    /// <param name="count">Number of recent metrics to retrieve.</param>
    /// <returns>The most recent AnalysisMetric records, limited to count.</returns>
    public async Task<IReadOnlyList<AnalysisMetric>> GetRecentAsync(int count) =>
        await _context.AnalysisMetrics.OrderByDescending(m => m.AnalyzedAt).Take(count).ToListAsync();

    /// <summary>
    /// Gets a summary of performance metrics for the entire library.
    /// </summary>
    /// <remarks>
    /// Computes: average time per photo, fastest/slowest photo, throughput (photos/minute),
    /// average tag count per photo, and average description length. Returns null if no metrics exist.
    /// </remarks>
    /// <returns>A BatchMetricsSummary with aggregate statistics, or null if no metrics are recorded.</returns>
    public async Task<BatchMetricsSummary?> GetSummaryAsync()
    {
        var all = await _context.AnalysisMetrics.ToListAsync();
        return BuildSummary(all, null);
    }

    /// <summary>
    /// Gets a summary of performance metrics for a specific batch of photos.
    /// </summary>
    /// <remarks>
    /// Filters metrics to the given photo IDs and includes filename lookups for the fastest/slowest photos
    /// (falling back to GUID prefixes if filenames are not found). Used to populate batch import result summaries.
    /// </remarks>
    /// <param name="photoIds">IDs of the photos to summarize.</param>
    /// <returns>A BatchMetricsSummary with aggregate statistics for the batch, or null if no metrics exist for these photos.</returns>
    public async Task<BatchMetricsSummary?> GetBatchSummaryAsync(IReadOnlyList<Guid> photoIds)
    {
        var metrics = await _context.AnalysisMetrics
            .Where(m => photoIds.Contains(m.PhotoId))
            .ToListAsync();

        // Resolve filenames for fastest/slowest from MediaFiles
        var fileMap = await _context.MediaFiles
            .Where(f => photoIds.Contains(f.Id))
            .Select(f => new { f.Id, f.FileName })
            .ToDictionaryAsync(f => f.Id, f => f.FileName);

        return BuildSummary(metrics, fileMap);
    }

    private static BatchMetricsSummary? BuildSummary(
        IReadOnlyList<AnalysisMetric> metrics,
        Dictionary<Guid, string>? fileMap)
    {
        if (metrics.Count == 0) return null;

        var fastest = metrics.MinBy(m => m.TotalAnalysisMs)!;
        var slowest = metrics.MaxBy(m => m.TotalAnalysisMs)!;
        long totalMs = metrics.Sum(m => m.TotalAnalysisMs);

        return new BatchMetricsSummary(
            PhotoCount:          metrics.Count,
            AvgTotalMs:          metrics.Average(m => m.TotalAnalysisMs),
            FastestMs:           fastest.TotalAnalysisMs,
            SlowestMs:           slowest.TotalAnalysisMs,
            FastestFile:         fileMap?.GetValueOrDefault(fastest.PhotoId) ?? fastest.PhotoId.ToString()[..8],
            SlowestFile:         fileMap?.GetValueOrDefault(slowest.PhotoId) ?? slowest.PhotoId.ToString()[..8],
            TotalMs:             totalMs,
            PhotosPerMinute:     totalMs > 0 ? metrics.Count / (totalMs / 60_000.0) : 0,
            AvgTagCount:         metrics.Average(m => m.TagCount),
            AvgDescriptionLength: metrics.Average(m => m.DescriptionLength)
        );
    }
}
