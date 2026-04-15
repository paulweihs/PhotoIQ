using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class UpgradePromptWindow : Window
{
    public UpgradePromptWindow(int currentCount)
    {
        InitializeComponent();
        var vm = new UpgradePromptViewModel(currentCount);
        vm.CloseRequested = Close;
        DataContext = vm;
    }
}
