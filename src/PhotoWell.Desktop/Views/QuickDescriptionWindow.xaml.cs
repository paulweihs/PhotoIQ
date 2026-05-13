using System.Windows;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// Minimal "AI Description" window opened from the Windows Explorer context menu
/// (--describe "path") or programmatically from the main application.
/// </summary>
/// <remarks>
/// The window is deliberately lightweight — it creates its own DI scope to access the
/// repository rather than depending on the main window being open, so it can be used as
/// a standalone entry point when the main UI is not visible.
/// </remarks>
public partial class QuickDescriptionWindow : Window
{
    public QuickDescriptionWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is QuickDescriptionViewModel vm)
            vm.RequestClose += () => Dispatcher.InvokeAsync(Close);
    }
}
