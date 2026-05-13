using System.Windows;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

public partial class BackupWindow : Window
{
    public BackupWindow(BackupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RestoreCompleted   += Close;
        viewModel.BundleImportCompleted += Close;
    }
}
