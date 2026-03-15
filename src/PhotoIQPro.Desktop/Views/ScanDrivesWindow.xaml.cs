// File: PhotoIQPro.Desktop/Views/ScanDrivesWindow.xaml.cs
using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class ScanDrivesWindow : Window
{
    public ScanDrivesWindow(ScanDrivesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
