using System.Windows;
using System.Windows.Controls;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class ManageLibrariesWindow : Window
{
    private readonly LibraryViewModel _vm;

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

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedNode = e.NewValue as LibraryNodeViewModel;
    }
}
