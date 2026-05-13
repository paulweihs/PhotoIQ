using System.Windows;
using System.Windows.Input;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// A generic single-line text input dialog window.
/// </summary>
/// <remarks>
/// Used for prompting the user to enter or edit text (e.g., library name, album name, tag name).
/// Returns DialogResult=true if the user clicks OK or presses Enter; false if they click Cancel or press Escape.
/// The input text is available via the InputText property.
/// </remarks>
public partial class TextInputDialog : Window
{
    /// <summary>Gets the text entered by the user in the input box.</summary>
    public string InputText => InputBox.Text;

    /// <summary>
    /// Initializes a new instance of the TextInputDialog.
    /// </summary>
    /// <param name="title">The window title (e.g., "Create Library").</param>
    /// <param name="label">The label text displayed above the input box (e.g., "Library name:").</param>
    /// <param name="initial">Optional initial text to populate the input box (default empty string).</param>
    public TextInputDialog(string title, string label, string initial = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        InputBox.Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>Handles the OK button click; closes the dialog with DialogResult=true.</summary>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>Handles the Cancel button click; closes the dialog with DialogResult=false.</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Handles keyboard shortcuts: Enter confirms, Escape cancels.</summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        else if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
