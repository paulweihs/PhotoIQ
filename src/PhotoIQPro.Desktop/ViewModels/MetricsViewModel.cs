using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

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
    public bool NoData => !HasData;

    // Live batch summary set by MainViewModel during an active analysis run
    [ObservableProperty] private bool   _isBatchRunning     = false;
    private string _batchStatusText = "";
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

    public MetricsViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _ = RefreshAsync();
    }

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
