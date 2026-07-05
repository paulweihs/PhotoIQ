using System.ComponentModel;
using System.Windows;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

public partial class ImportDetailWindow : Window
{
    public ImportDetailWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImportProgressViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        if (DataContext is ImportProgressViewModel vm)
            vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportProgressViewModel.IsRunning))
        {
            var vm = (ImportProgressViewModel)DataContext;
            if (!vm.IsRunning)
                Dispatcher.Invoke(Close);
        }
    }
}
