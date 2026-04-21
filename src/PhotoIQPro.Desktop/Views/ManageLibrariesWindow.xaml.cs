using System.Windows;
using System.Windows.Controls;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Dialog window for managing libraries, albums, and photo organization.
/// </summary>
/// <remarks>
/// Displays a hierarchical tree view of all libraries and their albums.
/// Users can:
/// - Create new libraries and albums
/// - Rename or delete libraries/albums
/// - Select an album to view its photos in the main gallery
/// - Manage album covers and descriptions
///
/// The window uses a tree control to show the library/album hierarchy.
/// Selecting an album and clicking "View" closes the dialog and notifies
/// MainViewModel to navigate to that album.
/// </remarks>
public partial class ManageLibrariesWindow : Window
{
    private readonly LibraryViewModel _vm;

    /// <summary>Initializes a new instance of the ManageLibrariesWindow.</summary>
    /// <param name="vm">The LibraryViewModel that manages library and album data.</param>
    public ManageLibrariesWindow(LibraryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.ViewAlbumRequested += (albumId, albumName) =>
        {
            DialogResult = true;
            Close();
            // MainViewModel handles the navigation after window closes via the event args
            // stored in the Tag property.
            Tag = (albumId, albumName);
        };
    }

    /// <summary>Handles tree node selection; updates the SelectedNode property in the ViewModel.</summary>
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedNode = e.NewValue as LibraryNodeViewModel;
    }
}
