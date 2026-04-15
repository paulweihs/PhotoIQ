using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

public class AnalysisMetricRepository : IAnalysisMetricRepository
{
    private readonly PhotoIQContext _context;
    public AnalysisMetricRepository(PhotoIQContext context) => _context = context;

    public async Task AddAsync(AnalysisMetric metric)
    {
        await _context.AnalysisMetrics.AddAsync(metric);
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AnalysisMetric>> GetAllAsync() =>
        await _context.AnalysisMetrics.OrderByDescending(m => m.AnalyzedAt).ToListAsync();

    public async Task<IReadOnlyList<AnalysisMetric>> GetRecentAsync(int count) =>
        await _context.AnalysisMetrics.OrderByDescending(m => m.AnalyzedAt).Take(count).ToListAsync();

    public async Task<BatchMetricsSummary?> GetSummaryAsync()
    {
        var all = await _context.AnalysisMetrics.ToListAsync();
        return BuildSummary(all, null);
    }

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
