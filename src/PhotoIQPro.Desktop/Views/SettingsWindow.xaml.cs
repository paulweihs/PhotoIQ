using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // SaveCommand already ran via the Command binding before this Click handler fires.
        // Prompt for restart if AI engine settings changed.
        var vm = (SettingsViewModel)DataContext;
        if (vm.RequiresRestart)
        {
            var result = MessageBox.Show(
                "AI engine settings have changed and will take effect after a restart.\n\nRestart PhotoIQ Pro now?",
                "Restart Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
                System.Windows.Application.Current.Shutdown();
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
