using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Settings dialog window for configuring application preferences.
/// </summary>
/// <remarks>
/// Allows users to customize:
/// - Thumbnail size
/// - AI model selection (vision model, CLIP model)
/// - Tier settings (Express vs Standard)
/// - Other preferences
///
/// Prompts for an application restart if AI engine settings are changed, since model
/// reloading requires process restart.
/// </remarks>
public partial class SettingsWindow : Window
{
    /// <summary>Initializes a new instance of the SettingsWindow.</summary>
    /// <param name="vm">The SettingsViewModel containing all settings and save logic.</param>
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Saves all settings and prompts for restart if AI engine settings changed.</summary>
    /// <remarks>
    /// The SaveCommand on the ViewModel runs first (via XAML Command binding).
    /// This handler runs after and checks if RequiresRestart is true; if so, offers
    /// to restart the application immediately to apply the changes.
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
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

    /// <summary>Closes the settings window without saving changes.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
