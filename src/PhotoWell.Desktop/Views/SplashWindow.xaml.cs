using System.Reflection;
using System.Windows;

namespace PhotoWell.Desktop.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
    }

    public void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }

    /// <summary>
    /// Switches the splash to determinate download mode and updates progress.
    /// Call with fraction 0–1 during download; the indeterminate bar hides
    /// and the green download bar becomes visible.
    /// </summary>
    public void SetDownloadProgress(double fraction, string status)
    {
        Dispatcher.Invoke(() =>
        {
            IndeterminateBar.Visibility = System.Windows.Visibility.Collapsed;
            DownloadBar.Visibility      = System.Windows.Visibility.Visible;
            DownloadBar.Value           = fraction;
            StatusText.Text             = status;
        });
    }

    /// <summary>Returns the splash to indeterminate spinner mode.</summary>
    public void ResetToSpinner()
    {
        Dispatcher.Invoke(() =>
        {
            DownloadBar.Visibility      = System.Windows.Visibility.Collapsed;
            IndeterminateBar.Visibility = System.Windows.Visibility.Visible;
        });
    }
}
