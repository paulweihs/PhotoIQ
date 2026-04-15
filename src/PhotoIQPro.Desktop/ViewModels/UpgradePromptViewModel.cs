using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Common;

namespace PhotoIQPro.Desktop.ViewModels;

public partial class UpgradePromptViewModel : ObservableObject
{
    public string LibraryCountText { get; }
    public string LimitText        { get; }

    public Action? CloseRequested { get; set; }

    public UpgradePromptViewModel(int currentCount)
    {
        LibraryCountText = $"{currentCount:N0}";
        LimitText        = $"{AppSettings.ExpressLibraryLimit:N0}";
    }

    [RelayCommand]
    private void StayOnExpress() => CloseRequested?.Invoke();

    [RelayCommand]
    private void UpgradeToStandard()
    {
        Process.Start(new ProcessStartInfo(AppSettings.UpgradeUrl) { UseShellExecute = true });
        CloseRequested?.Invoke();
    }
}
