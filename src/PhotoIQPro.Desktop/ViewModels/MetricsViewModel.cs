using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Metrics dashboard window, displaying AI analysis performance statistics.
/// </summary>
/// <remarks>
/// Shows aggregated metrics across all completed analysis jobs:
/// - Average/fastest/slowest photo analysis times
/// - Total photos analyzed and analysis throughput (photos/minute)
/// - Average tags and description length per photo
/// - Live batch status during an active analysis run (set by MainViewModel callbacks)
///
/// Metrics are loaded on init and can be manually refreshed via RefreshCommand.
/// </remarks>
public partial class MetricsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    [ObservableProperty] private string _avgTimeText        = "—";
    [ObservableProperty] private string _fastestText        = "—";
    [ObservableProperty] private string _slowestText        = "—";
    [ObservableProperty] private string _totalTimeText      = "—";
    [ObservableProperty] private string _photosPerMinText   = "—";
    [ObservableProperty] private string _avgTagsText        = "—";
    [ObservableProperty] private string _avgDescLenText     = "—";
    [ObservableProperty] private string _photoCountText     = "—";
    [ObservableProperty] private bool   _hasData            = false;

    /// <summary>True if no analysis metrics have been recorded yet.</summary>
    public bool NoData => !HasData;

    /// <summary>True if a batch analysis job is currently running (updated by MainViewModel callbacks).</summary>
    [ObservableProperty] private bool   _isBatchRunning     = false;

    private string _batchStatusText = "";

    /// <summary>
    /// Live status message during batch analysis (set by MainViewModel during ReanalyzeAllAsync, etc.).
    /// Setting non-empty value sets IsBatchRunning=true; clearing sets it to false.
    /// </summary>
    public string BatchStatusText
    {
        get => _batchStatusText;
        set
        {
            _batchStatusText = value;
            IsBatchRunning   = value.Length > 0;
            OnPropertyChanged();
        }
    }

    /// <summary>Initializes a new instance of the MetricsViewModel.</summary>
    /// <param name="scopeFactory">Factory for creating DI scopes to access the analysis metric repository.</param>
    public MetricsViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _ = RefreshAsync();
    }

    /// <summary>Reloads all metrics from the database and updates display strings.</summary>
    /// <remarks>Called on startup and whenever the user clicks the Refresh button.</remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var repo        = scope.ServiceProvider.GetRequiredService<IAnalysisMetricRepository>();
        var summary     = await repo.GetSummaryAsync();
        if (summary == null)
        {
            HasData = false;
            OnPropertyChanged(nameof(NoData));
            return;
        }

        HasData = true;
        OnPropertyChanged(nameof(NoData));
        PhotoCountText   = $"{summary.PhotoCount:N0} photos";
        AvgTimeText      = FormatMs(summary.AvgTotalMs);
        FastestText      = $"{FormatMs(summary.FastestMs)} — {summary.FastestFile}";
        SlowestText      = $"{FormatMs(summary.SlowestMs)} — {summary.SlowestFile}";
        TotalTimeText    = FormatMs(summary.TotalMs);
        PhotosPerMinText = $"{summary.PhotosPerMinute:F1} / min";
        AvgTagsText      = $"{summary.AvgTagCount:F1}";
        AvgDescLenText   = $"{summary.AvgDescriptionLength:F0} chars";
    }

    /// <summary>Clears the live batch status and refreshes metrics (called when a batch job completes).</summary>
    public void ClearBatchStatus()
    {
        IsBatchRunning  = false;
        BatchStatusText = "";
        _ = RefreshAsync();
    }

    private static string FormatMs(double ms) => ms switch
    {
        < 1_000             => $"{ms:F0} ms",
        < 60_000            => $"{ms / 1_000:F1} s",
        _                   => $"{ms / 60_000:F1} min"
    };

    private static string FormatMs(long ms) => FormatMs((double)ms);
}
