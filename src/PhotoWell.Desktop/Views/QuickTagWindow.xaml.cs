using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

public partial class QuickTagWindow : System.Windows.Window
{
    public QuickTagWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is QuickTagViewModel vm)
                vm.RequestClose += Close;
        };
    }
}
