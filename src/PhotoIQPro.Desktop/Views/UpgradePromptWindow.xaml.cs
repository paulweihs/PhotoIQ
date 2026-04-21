using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Dialog that prompts users to upgrade from Express to Standard tier when approaching the photo limit.
/// </summary>
/// <remarks>
/// Shown when the user's library reaches 85% of the Express tier limit (5,000 photos),
/// and again on any subsequent import attempt that would exceed the limit.
///
/// The dialog displays the current library size vs. the Express tier limit and offers two options:
/// - Stay on Express: dismiss the prompt and continue
/// - Upgrade to Standard: opens the upgrade URL in the default browser
/// </remarks>
public partial class UpgradePromptWindow : Window
{
    /// <summary>Initializes a new instance of the UpgradePromptWindow.</summary>
    /// <param name="currentCount">The current number of photos in the library.</param>
    public UpgradePromptWindow(int currentCount)
    {
        InitializeComponent();
        var vm = new UpgradePromptViewModel(currentCount);
        vm.CloseRequested = Close;
        DataContext = vm;
    }
}
