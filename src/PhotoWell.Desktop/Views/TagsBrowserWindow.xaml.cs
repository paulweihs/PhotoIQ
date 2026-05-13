using System.Windows;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// Window for browsing all tags in the library with photo counts.
/// </summary>
/// <remarks>
/// Displays a list of all tags used in the library, sorted by frequency (most-used first).
/// Each tag shows how many photos are tagged with it.
/// Users can search for tags by name and click one to filter the main gallery.
///
/// The TagsBrowserViewModel loads the tag list asynchronously on window open.
/// </remarks>
public partial class TagsBrowserWindow : Window
{
    /// <summary>Initializes a new instance of the TagsBrowserWindow.</summary>
    /// <param name="vm">The TagsBrowserViewModel that loads and manages tag data.</param>
    public TagsBrowserWindow(TagsBrowserViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
