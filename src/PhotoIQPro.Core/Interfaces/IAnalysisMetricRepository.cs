using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IAnalysisMetricRepository
{
    Task AddAsync(AnalysisMetric metric);
    Task<IReadOnlyList<AnalysisMetric>> GetAllAsync();
    Task<IReadOnlyList<AnalysisMetric>> GetRecentAsync(int count);
    Task<BatchMetricsSummary?> GetSummaryAsync();
    Task<BatchMetricsSummary?> GetBatchSummaryAsync(IReadOnlyList<Guid> photoIds);
}

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
