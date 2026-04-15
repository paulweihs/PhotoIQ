using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PhotoIQPro.Desktop.Views;

public partial class ExcludeFolderDialog : Window
{
    /// <summary>True = exclude folder and all subfolders; false = this folder only.</summary>
    public bool IncludeSubfolders { get; private set; }

    public ExcludeFolderDialog(string folderPath, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        FolderNameText.Text = $"Exclude \"{Path.GetFileName(folderPath)}\"";
    }

    private void FolderAndSubfolders_Click(object sender, RoutedEventArgs e)
    {
        IncludeSubfolders = true;
        DialogResult = true;
        Close();
    }

    private void FolderOnly_Click(object sender, RoutedEventArgs e)
    {
        IncludeSubfolders = false;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
