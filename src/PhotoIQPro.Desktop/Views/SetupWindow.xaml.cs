using System.ComponentModel;
using System.Windows;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;
    private bool _allowClose = false;

    public event Action? SetupCompleted;

    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.SetupComplete += () =>
        {
            _allowClose = true;
            SetupCompleted?.Invoke();
            Close();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => _ = _vm.RunAsync();

    private void OnContinueAnyway(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        SetupCompleted?.Invoke();
        Close();
    }

    // Block window-close (Alt+F4) while setup is in progress.
    // Allow it once complete or if the user explicitly clicks Continue Anyway.
    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_allowClose)
            e.Cancel = true;
    }
}
