using System.Windows;
using System.Windows.Input;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class FindDuplicatesWindow : Window
{
    private readonly FindDuplicatesViewModel _vm;

    public FindDuplicatesWindow(FindDuplicatesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.ScanAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
