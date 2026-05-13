using System.Windows;
using System.Windows.Input;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// Dialog window for finding and deleting duplicate photos in the library.
/// </summary>
/// <remarks>
/// Scans the library for:
/// - Exact hash duplicates (identical file SHA-256)
/// - RAW+JPEG pairs (same photo in different formats, e.g., DNG + JPG from same camera)
///
/// Users can review groups of duplicates and select which copies to delete,
/// keeping the one they prefer. The FindDuplicatesViewModel loads duplicate groups
/// asynchronously on window load.
///
/// The window supports Escape key to close, and provides a Close button.
/// </remarks>
public partial class FindDuplicatesWindow : Window
{
    private readonly FindDuplicatesViewModel _vm;

    /// <summary>Initializes a new instance of the FindDuplicatesWindow.</summary>
    /// <param name="vm">The FindDuplicatesViewModel that scans for and manages duplicate groups.</param>
    public FindDuplicatesWindow(FindDuplicatesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.ScanAsync();
    }

    /// <summary>Closes the window when the Escape key is pressed.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    /// <summary>Clears all ignored folders and re-scans so previously hidden groups reappear.</summary>
    private async void OnClearIgnoredFoldersClick(object sender, RoutedEventArgs e)
        => await _vm.ClearIgnoredFolders();

    /// <summary>Closes the find duplicates window when the user clicks the close button.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
