using System.Windows;
using System.Windows.Input;

namespace PhotoIQPro.Desktop.Views;

public partial class TextInputDialog : Window
{
    public string InputText => InputBox.Text;

    public TextInputDialog(string title, string label, string initial = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        InputBox.Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        else if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
