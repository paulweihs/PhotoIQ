using System.ComponentModel;
using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Modal setup window shown on first application launch.
/// </summary>
/// <remarks>
/// Initializes the application by:
/// - Creating the database if it doesn't exist
/// - Loading AI models (CLIP, face detection, face recognition)
/// - Scanning for initial photos (optional)
///
/// The window is modal and cannot be closed (Alt+F4 is blocked) until setup completes
/// or the user explicitly clicks "Continue Anyway" (if setup fails or is skipped).
///
/// Once setup completes, the window closes and the main application window opens.
/// </remarks>
public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;
    private bool _allowClose = false;

    /// <summary>Raised when setup completes (either successfully or user chose to continue anyway).</summary>
    public event Action? SetupCompleted;

    /// <summary>Initializes a new instance of the SetupWindow.</summary>
    /// <param name="vm">The SetupViewModel that orchestrates initialization.</param>
    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.SetupComplete += () =>
        {
            _allowClose = true;
            SetupCompleted?.Invoke();
            Close();
        };
    }

    /// <summary>Starts the setup workflow when the window is loaded.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
        => _ = _vm.RunAsync();

    /// <summary>Allows closing the setup window even if initialization hasn't fully completed.</summary>
    /// <remarks>User must explicitly click "Continue Anyway" to bypass setup (e.g., if model download fails).</remarks>
    private void OnContinueAnyway(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        SetupCompleted?.Invoke();
        Close();
    }

    /// <summary>Blocks window close (Alt+F4) while setup is in progress; only allows explicit user action.</summary>
    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_allowClose)
            e.Cancel = true;
    }
}
