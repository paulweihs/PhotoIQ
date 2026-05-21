using System.Windows;
using System.Windows.Controls;

namespace PhotoWell.Desktop.Views;

public partial class EditDateDialog : Window
{
    public DateTime? ResultDateTime { get; private set; }
    public bool WriteToFile { get; private set; }
    public bool CreateBackup { get; private set; }

    public EditDateDialog(DateTime? initialDate)
    {
        InitializeComponent();

        var dt = initialDate ?? DateTime.Now;
        DatePicker.SelectedDate = dt.Date;
        TimeBox.Text = dt.ToString("HH:mm:ss");
    }

    private void OnWriteToFileChanged(object sender, RoutedEventArgs e)
    {
        BackupCheck.IsEnabled = WriteToFileCheck.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DatePicker.SelectedDate is not { } date)
        {
            MessageBox.Show(this, "Please select a date.", "Edit Date", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseTime(TimeBox.Text.Trim(), out var timeSpan))
        {
            MessageBox.Show(this, "Time must be in HH:MM:SS format (24-hour).", "Edit Date",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultDateTime = date + timeSpan;
        WriteToFile = WriteToFileCheck.IsChecked == true;
        CreateBackup = BackupCheck.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParseTime(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var parts = text.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return false;
        if (!int.TryParse(parts[0], out int h) || h < 0 || h > 23) return false;
        if (!int.TryParse(parts[1], out int m) || m < 0 || m > 59) return false;
        int s = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out s) || s < 0 || s > 59)) return false;
        result = new TimeSpan(h, m, s);
        return true;
    }
}
