using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Common;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// ViewModel for the upgrade prompt dialog, shown when the user approaches or exceeds the Express tier photo limit.
/// </summary>
/// <remarks>
/// The prompt displays the current library size vs. the Express tier limit and offers two actions:
/// - Stay on Express: dismiss the prompt and continue
/// - Upgrade to Standard: opens the upgrade URL in the default browser
///
/// The prompt is shown by MainViewModel when PhotoCount reaches 85% of ExpressLibraryLimit,
/// and again on each subsequent attempt to import beyond the limit.
/// </remarks>
public partial class UpgradePromptViewModel : ObservableObject
{
    /// <summary>The current number of photos in the library, formatted with thousands separators.</summary>
    public string LibraryCountText { get; }

    /// <summary>The Express tier photo limit (from AppSettings.ExpressLibraryLimit), formatted with separators.</summary>
    public string LimitText { get; }

    /// <summary>Raised when the user dismisses the prompt (either by staying or upgrading).</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Initializes a new instance of the UpgradePromptViewModel.</summary>
    /// <param name="currentCount">The current number of photos in the library.</param>
    public UpgradePromptViewModel(int currentCount)
    {
        LibraryCountText = $"{currentCount:N0}";
        LimitText = $"{AppSettings.ExpressLibraryLimit:N0}";
    }

    /// <summary>User chose to stay on Express tier; closes the prompt.</summary>
    [RelayCommand]
    private void StayOnExpress() => CloseRequested?.Invoke();

    /// <summary>User chose to upgrade; opens the upgrade URL in the default browser and closes the prompt.</summary>
    [RelayCommand]
    private void UpgradeToStandard()
    {
        Process.Start(new ProcessStartInfo(AppSettings.UpgradeUrl) { UseShellExecute = true });
        CloseRequested?.Invoke();
    }
}
