using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class TagsBrowserWindow : Window
{
    public TagsBrowserWindow(TagsBrowserViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
