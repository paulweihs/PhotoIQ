using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoIQPro.Desktop.ViewModels;

namespace PhotoIQPro.Desktop.Views;

public partial class PeopleWindow : Window
{
    public PeopleWindow(PeopleViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    // Enter confirms the name edit; Escape cancels it.
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not PersonCardViewModel card) return;
        var vm = (PeopleViewModel)DataContext;

        if (e.Key == Key.Enter)
        {
            _ = vm.CommitEditCommand.ExecuteAsync(card);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelEditCommand.Execute(card);
            e.Handled = true;
        }
    }
}
