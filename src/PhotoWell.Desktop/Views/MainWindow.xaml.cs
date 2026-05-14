using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Core.Models;
using PhotoWell.Desktop.ViewModels;
using PhotoWell.Desktop.Views.Controls;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// The main application window containing the photo gallery, detail panel, and library sidebar.
/// </summary>
/// <remarks>
/// Handles:
/// - Gallery navigation via keyboard (arrow keys, Enter to open viewer)
/// - Multi-selection with Ctrl+click and Shift+click, single-click to clear
/// - Drag-drop of photos onto albums and libraries in the sidebar
/// - Device change notifications (drives connected/disconnected) to update offline status
/// - Search box and tag editor keyboard shortcuts
/// - Double-click to open photo viewer
///
/// The DataContext is set to MainViewModel via App.Services dependency injection.
/// </remarks>
public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private HwndSource? _hwndSource;
    private Action<MediaFile>? _scrollHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _vmPropertyChangedHandler;

    private Point   _dragStartPoint;
    private bool    _isDragPending;
    private const string DragFormat = "PhotoWell.PhotoIds";

    private System.Windows.Threading.DispatcherTimer? _resizeScrollTimer;

    public MainWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = vm;

        _scrollHandler = mf =>
        {
            PhotoGrid.UpdateLayout();
            PhotoGrid.ScrollIntoView(mf);
        };
        _vmPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsEditingDescription) && vm.IsEditingDescription)
                Dispatcher.InvokeAsync(() => DescriptionEditBox.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // Wire tip toast to its ViewModel
        var tipToastVm = App.Services.GetRequiredService<TipToastViewModel>();
        TipToast.DataContext = tipToastVm;

        vm.ScrollIntoViewRequested += _scrollHandler;
        vm.ScrollToTopRequested    += () =>
        {
            // Scroll the virtualizing panel to the top before the new result set is shown.
            var sv = FindVisualChild<System.Windows.Controls.ScrollViewer>(PhotoGrid);
            sv?.ScrollToTop();
        };
        vm.PropertyChanged += _vmPropertyChangedHandler;

        _resizeScrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _resizeScrollTimer.Tick += (_, _) =>
        {
            _resizeScrollTimer.Stop();
            if (PhotoGrid.SelectedItem != null)
            {
                PhotoGrid.UpdateLayout();
                PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
            }
        };
        PhotoGrid.SizeChanged += (_, _) =>
        {
            _resizeScrollTimer.Stop();
            _resizeScrollTimer.Start();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.ScrollIntoViewRequested -= _scrollHandler;
        vm.PropertyChanged -= _vmPropertyChangedHandler;
        _resizeScrollTimer?.Stop();
        _hwndSource?.RemoveHook(WndProc);
        vm.Dispose();
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.Progress.IsRunning)
        {
            string title, message;
            if (vm.Progress.IsQueueImport || vm.Progress.IsResumable)
            {
                title   = vm.Progress.IsQueueImport ? "Import in Progress" : "Operation in Progress";
                message = $"{(vm.Progress.IsQueueImport ? "An import" : vm.Progress.Heading)} is currently running.\n\n" +
                          "If you exit now, progress will be saved and will automatically resume the next time you launch PhotoWell.\n\n" +
                          "Do you want to exit?";
            }
            else
            {
                var opName = string.IsNullOrWhiteSpace(vm.Progress.Heading) ? "A background operation" : vm.Progress.Heading;
                title   = "Operation in Progress";
                message = $"{opName} is currently running.\n\n" +
                          "If you exit now, progress will be lost and will not automatically resume.\n\n" +
                          "Do you want to exit?";
            }

            var result = MessageBox.Show(message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            vm.Progress.ForceCancel();
        }
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();
            if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                _ = ((MainViewModel)DataContext).RefreshOfflineStatusAsync();
        }
        return IntPtr.Zero;
    }

    private void PathText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => TextBoxHelpers.ScrollToHome(sender, e);

    private void SelectableText_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => TextBoxHelpers.SelectAllOnFirstFocus(sender, e);

    private void OnPhotoDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.SelectedMediaFile != null)
            vm.OpenPhotoViewerCommand.Execute(null);
    }

    // Arrow key gallery navigation. Skips when focus is inside a TextBox so search/path
    // fields handle Left/Right themselves.
    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            vm.ExecuteSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;

        var vm = (MainViewModel)DataContext;
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            vm.NavigateNextCommand.Execute(null);
            if (PhotoGrid.SelectedItem != null) PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            vm.NavigatePreviousCommand.Execute(null);
            if (PhotoGrid.SelectedItem != null) PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            // Jump a full row: calculate how many items fit across the panel width.
            // Item container = ThumbnailSize + 8 (4px margin each side).
            // Subtract the vertical scrollbar width since the wrap panel can't use that space.
            double itemWidth = vm.ThumbnailSize + 8;
            double panelWidth = Math.Max(itemWidth,
                PhotoGrid.ActualWidth - System.Windows.SystemParameters.VerticalScrollBarWidth);
            int columns = Math.Max(1, (int)(panelWidth / itemWidth));
            int offset  = e.Key == Key.Down ? columns : -columns;
            vm.NavigateByOffsetCommand.Execute(offset);
            if (PhotoGrid.SelectedItem != null) PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown || e.Key == Key.PageUp)
        {
            double itemWidth  = vm.ThumbnailSize + 8;
            double itemHeight = vm.ThumbnailSize + 8;
            double panelWidth = Math.Max(itemWidth,
                PhotoGrid.ActualWidth - System.Windows.SystemParameters.VerticalScrollBarWidth);
            int columns = Math.Max(1, (int)(panelWidth / itemWidth));
            int rows    = Math.Max(1, (int)(PhotoGrid.ActualHeight / itemHeight));
            int offset  = e.Key == Key.PageDown ? columns * rows : -(columns * rows);
            vm.NavigateByOffsetCommand.Execute(offset);
            if (PhotoGrid.SelectedItem != null) PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None)
        {
            vm.ToggleFavoriteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            if (vm.SelectedMediaFile != null)
                vm.OpenPhotoViewerCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Handles all left-button clicks on the gallery grid.
    // Plain click: clear multi-selection, let ListBox set SelectedItem normally.
    // Ctrl+click: toggle item in multi-selection without changing SelectedItem.
    // Shift+click: range-select from anchor.
    private void OnPhotoGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_isDragPending) return;

        var pos  = e.GetPosition(PhotoGrid);
        var diff = _dragStartPoint - pos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isDragPending = false;

        var vm = (MainViewModel)DataContext;
        var ids = vm.HasMultiSelection
            ? vm.MultiSelectedItems.Select(m => m.Id).ToList()
            : vm.SelectedMediaFile != null ? [vm.SelectedMediaFile.Id] : (List<Guid>)[];

        if (ids.Count == 0) return;

        var data = new DataObject(DragFormat, string.Join(",", ids));
        DragDrop.DoDragDrop(PhotoGrid, data, DragDropEffects.Copy);
    }

    private void OnPhotoGridPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragPending = false;
    }

    private void OnPhotoGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(PhotoGrid);
        _isDragPending  = true;

        bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);

        if (element is not ListBoxItem { DataContext: MediaFile file }) return;

        var vm = (MainViewModel)DataContext;

        if (shift)
        {
            vm.SelectRangeCommand.Execute(file);
            e.Handled = true;
        }
        else if (ctrl)
        {
            vm.ToggleSelectionCommand.Execute(file);
            e.Handled = true;  // prevent ListBox from changing SelectedItem
        }
        else
        {
            // Plain click — clear multi-selection; let ListBox handle SelectedItem binding.
            vm.ClearSelectionCommand.Execute(null);
        }
    }

    private void AllMetadataExpander_Expanded(object sender, RoutedEventArgs e)
        => ((MainViewModel)DataContext).LoadAllMetadataCommand.Execute(null);

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
        => SearchBox.Focus();

    private void OnOfflineStatusClick(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        var roots = vm.MediaFiles
            .Where(m => m.IsOffline)
            .Select(m => System.IO.Path.GetPathRoot(m.FilePath) ?? "")
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();

        var detail = roots.Count > 0
            ? string.Join("\n", roots)
            : "Unknown drives";

        MessageBox.Show(
            $"The following drives are not currently connected:\n\n{detail}\n\n" +
            "Reconnect the drive to open, re-analyze, or delete these photos.",
            "Offline Photos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnNewTagBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ((MainViewModel)DataContext).AddTagCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Right-click on a thumbnail selects it (single mode) so context menu commands
    // have a valid SelectedMediaFile. Does not clear multi-selection if active.
    private void OnPhotoGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);

        if (element is ListBoxItem { DataContext: MediaFile file })
        {
            var vm = (MainViewModel)DataContext;
            if (!vm.HasMultiSelection)
                vm.SelectedMediaFile = file;
        }
    }

    // ── Batch tag popup ──────────────────────────────────────────────────────

    private void OnAddTagPopupOpened(object sender, EventArgs e)
        => Dispatcher.InvokeAsync(() => BatchTagBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private void OnBatchTagKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (e.Key == Key.Enter)
        {
            // If a suggestion is highlighted, accept it; otherwise confirm current text.
            if (TagSuggestionList.SelectedItem is string suggestion)
            {
                vm.SelectTagSuggestionCommand.Execute(suggestion);
                TagSuggestionList.SelectedIndex = -1;
            }
            else
            {
                vm.ConfirmBatchTagCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseAddTagPopupCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && TagSuggestionList.Items.Count > 0)
        {
            TagSuggestionList.Focus();
            TagSuggestionList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (TagSuggestionList.SelectedItem is string tag)
        {
            ((MainViewModel)DataContext).SelectTagSuggestionCommand.Execute(tag);
            BatchTagBox.Focus();
        }
    }

    // ── Sidebar album drag-drop ──────────────────────────────────────────────

    private static bool HasPhotoIds(DragEventArgs e)
        => e.Data.GetDataPresent(DragFormat);

    private void OnAlbumButtonDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPhotoIds(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAlbumButtonDragEnter(object sender, DragEventArgs e)
    {
        if (!HasPhotoIds(e)) { e.Effects = DragDropEffects.None; return; }
        if (sender is Button btn) btn.Opacity = 0.7;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnAlbumButtonDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) btn.Opacity = 1.0;
    }

    private void OnLibraryHeaderDragEnter(object sender, DragEventArgs e)
    {
        if (!HasPhotoIds(e)) { e.Effects = DragDropEffects.None; return; }
        if (sender is Border b) b.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(60, 137, 180, 250));
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnLibraryHeaderDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void OnLibraryHeaderDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = System.Windows.Media.Brushes.Transparent;
        if (!HasPhotoIds(e)) return;

        if (sender is not Border { DataContext: LibraryNodeViewModel node } || !node.IsLibrary) return;

        var raw = e.Data.GetData(DragFormat) as string ?? "";
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                     .Where(g => g.HasValue)
                     .Select(g => g!.Value)
                     .ToList();

        if (ids.Count == 0) return;
        _ = ((MainViewModel)DataContext).DropPhotosOnLibraryAsync(node.Id, node.Name, ids);
        e.Handled = true;
    }

    private void OnAlbumButtonDrop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) btn.Opacity = 1.0;
        if (!HasPhotoIds(e)) return;

        if (sender is not Button { DataContext: LibraryNodeViewModel node } || node.IsLibrary) return;

        var raw = e.Data.GetData(DragFormat) as string ?? "";
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                     .Where(g => g.HasValue)
                     .Select(g => g!.Value)
                     .ToList();

        if (ids.Count == 0) return;

        _ = ((MainViewModel)DataContext).DropPhotosOnAlbumAsync(node.Id, node.Name, ids);
        e.Handled = true;
    }

    // ── Context-menu / IPC command entry points ──────────────────────────────

    /// <summary>
    /// Selects and scrolls to the photo at <paramref name="filePath"/> in the gallery.
    /// Called when the app is launched with --select "path" or receives the command via IPC.
    /// </summary>
    public void SelectFileInGallery(string filePath)
    {
        var vm = (MainViewModel)DataContext;
        var photo = vm.MediaFiles.FirstOrDefault(m =>
            string.Equals(m.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (photo is null) return;

        vm.SelectedMediaFile = photo;
        PhotoGrid.ScrollIntoView(photo);
    }

    /// <summary>
    /// Selects the photo at <paramref name="filePath"/> and runs Find Similar.
    /// Called when the app is launched with --similar "path" or receives the command via IPC.
    /// </summary>
    public void FindSimilarForFile(string filePath)
    {
        SelectFileInGallery(filePath);
        var vm = (MainViewModel)DataContext;
        if (vm.FindSimilarCommand.CanExecute(null))
            vm.FindSimilarCommand.Execute(null);
    }

    /// <summary>
    /// Selects the photo at <paramref name="filePath"/> and runs Find Related.
    /// Called when the app is launched with --related "path" or receives the command via IPC.
    /// </summary>
    public void FindRelatedForFile(string filePath)
    {
        SelectFileInGallery(filePath);
        var vm = (MainViewModel)DataContext;
        if (vm.FindRelatedImagesCommand.CanExecute(null))
            vm.FindRelatedImagesCommand.Execute(null);
    }

    /// <summary>
    /// Imports photos from <paramref name="folderPath"/> into the library.
    /// Called via IPC from the Windows Explorer context menu "Add Contents" action.
    /// </summary>
    public void ImportFolderFromShell(string folderPath, bool recursive)
        => ((MainViewModel)DataContext).ImportFolderFromShellCommand.Execute((folderPath, recursive));

    /// <summary>
    /// Removes photos in <paramref name="folderPath"/> from the library (does not delete files).
    /// Called via IPC from the Windows Explorer context menu "Remove Contents" action.
    /// </summary>
    public void RemoveFolderFromShell(string folderPath, bool recursive)
        => ((MainViewModel)DataContext).RemoveFolderFromShellCommand.Execute((folderPath, recursive));

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
