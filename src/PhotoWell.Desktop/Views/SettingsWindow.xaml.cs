using System.Windows;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

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

    /// <summary>
    /// Runs after SaveAsyncCommand completes. If AI engine settings changed, offers an immediate restart.
    /// </summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (vm.RequiresRestart)
        {
            var result = MessageBox.Show(
                "AI engine settings have changed and will take effect after a restart.\n\nRestart PhotoWell now?",
                "Restart Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
                System.Windows.Application.Current.Shutdown();
        }
    }

    /// <summary>Closes, prompting to save if there are unsaved changes.</summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        if (!vm.HasUnsavedChanges) { Close(); return; }

        var result = MessageBox.Show(
            "You have unsaved changes. Save before closing?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (result == MessageBoxResult.Cancel) return;
        if (result == MessageBoxResult.Yes)
        {
            // Fire-and-forget the async save; toast will show briefly before window closes.
            vm.SaveCommand.Execute(null);
            if (vm.RequiresRestart)
            {
                var restart = MessageBox.Show(
                    "AI engine settings have changed and will take effect after a restart.\n\nRestart PhotoWell now?",
                    "Restart Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (restart == MessageBoxResult.Yes)
                {
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
            }
        }
        Close();
    }
}
