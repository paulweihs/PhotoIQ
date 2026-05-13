// File: PhotoWell.Desktop/Views/ScanDrivesWindow.xaml.cs
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

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
    // Tracks the last-clicked row index (in current view order) so Shift+Click can compute a range.
    private int _lastClickedIndex = -1;

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

    /// <summary>
    /// Handles Shift+Click range selection in the Results folder list.
    /// Plain and Ctrl+Click fall through to the CheckBox as normal.
    /// </summary>
    private void OnResultsListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Walk up the visual tree to the ListBoxItem that owns the clicked element.
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is not ListBoxItem lbi) return;

        int index = _resultsListBox.ItemContainerGenerator.IndexFromContainer(lbi);
        if (index < 0) return;

        if (lbi.DataContext is not SelectableFolder folder || folder.IsExcluded) return;

        bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (shiftHeld && _lastClickedIndex >= 0)
        {
            // Range toggle: determine the state the clicked item *would* become, then apply to range.
            bool newState = !folder.IsSelected;
            var viewItems = _resultsListBox.Items.Cast<SelectableFolder>().ToList();

            int from = Math.Min(_lastClickedIndex, index);
            int to   = Math.Max(_lastClickedIndex, index);
            for (int i = from; i <= to; i++)
            {
                if (i < viewItems.Count && !viewItems[i].IsExcluded)
                    viewItems[i].IsSelected = newState;
            }

            e.Handled = true; // prevent the CheckBox from double-toggling the clicked item
        }

        // Always update the anchor, whether plain, Ctrl, or Shift click.
        _lastClickedIndex = index;
    }
}
