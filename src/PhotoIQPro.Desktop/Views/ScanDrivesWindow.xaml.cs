// File: PhotoIQPro.Desktop/Views/ScanDrivesWindow.xaml.cs
using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Dialog window for scanning drives and configuring media folder import settings.
/// </summary>
/// <remarks>
/// Displays all connected drives and allows users to:
/// - Select which drives to scan for photos
/// - Configure folder exclusion rules (skip system folders, user-defined exclusions)
/// - Control whether to scan recursively into subfolders
/// - Choose non-recursive scan mode to import only top-level files
///
/// Started by MainViewModel when the user clicks "Scan Drives" in the import menu.
/// The ScanDrivesViewModel coordinates the drive enumeration, exclusion rules, and import workflow.
/// </remarks>
public partial class ScanDrivesWindow : Window
{
    /// <summary>Initializes a new instance of the ScanDrivesWindow.</summary>
    /// <param name="viewModel">The ScanDrivesViewModel that manages drive enumeration and import configuration.</param>
    public ScanDrivesWindow(ScanDrivesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    /// <summary>Closes the scan drives window when the user clicks the close button.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
