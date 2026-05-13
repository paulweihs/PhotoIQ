using System.Windows;
using System.Windows.Media.Imaging;
using PhotoWell.Core.Models;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views;

/// <summary>
/// Sequential face review dialog — shown after inline face assignment.
/// Displays each unconfirmed face with a "Yes/No/Skip" interface for bulk confirmation.
/// </summary>
public partial class FaceReviewDialog : Window
{
    private readonly IReadOnlyList<(Face Face, MediaFile Photo)> _queue;
    private readonly string _personName;
    private readonly Guid   _personId;
    private readonly PhotoViewerViewModel _vm;
    private int _index;
    private int _confirmed;

    public FaceReviewDialog(IReadOnlyList<(Face, MediaFile)> queue, string personName, Guid personId, PhotoViewerViewModel vm)
    {
        InitializeComponent();
        _queue = queue;
        _personName = personName;
        _personId   = personId;
        _vm = vm;
        _index = 0;
        _confirmed = 0;
        NeverAskBtn.Content = $"Never ask about {personName}";
        ShowCurrent();
    }

    /// <summary>Display the current face in the review queue, or show the done state.</summary>
    private void ShowCurrent()
    {
        if (_index >= _queue.Count)
        {
            ShowDone();
            return;
        }

        var (face, photo) = _queue[_index];
        HeaderText.Text = $"Is this {_personName}?";
        CounterText.Text = $"{_index + 1} / {_queue.Count}";

        // Load face crop thumbnail
        FaceCropImage.Source = LoadImage(face.ThumbnailPath);

        // Load photo thumbnail — use small thumbnail if available, otherwise null
        PhotoThumbImage.Source = !string.IsNullOrEmpty(photo.ThumbnailSmall)
            ? LoadImage(photo.ThumbnailSmall)
            : null;

        MainPanel.Visibility = Visibility.Visible;
        DonePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Display the completion message and hide review UI.</summary>
    private void ShowDone()
    {
        MainPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneText.Text = $"Done! Confirmed {_confirmed} photo{(_confirmed == 1 ? "" : "s")} as {_personName}.";
        YesBtn.Visibility = Visibility.Collapsed;
        NoBtn.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Collapsed;
        CloseBtn.Visibility = Visibility.Visible;
    }

    /// <summary>Load an image from a file path, returning BitmapImage or null if load fails.</summary>
    private static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 160;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>User confirmed this face — mark as user-confirmed and the photo as reviewed, then advance.</summary>
    private async void YesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= _queue.Count) return;
        var (face, photo) = _queue[_index];
        await _vm.ConfirmFaceAsync(face.Id);
        await _vm.MarkFacesReviewedAsync(photo.Id);
        _confirmed++;
        _index++;
        ShowCurrent();
    }

    /// <summary>User rejected this face — unlink it and advance.</summary>
    private async void NoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= _queue.Count) return;
        var (face, _) = _queue[_index];
        await _vm.UnlinkFaceAsync(face.Id);
        _index++;
        ShowCurrent();
    }

    /// <summary>User skipped this face — advance without confirming or unlinking.</summary>
    private void SkipBtn_Click(object sender, RoutedEventArgs e)
    {
        _index++;
        ShowCurrent();
    }

    /// <summary>User chose "Never ask about [name]" — suppress future review dialogs for this person.</summary>
    private async void NeverAskBtn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SuppressPersonPromptAsync(_personId);
        DialogResult = true;
        Close();
    }

    /// <summary>Close the dialog when done.</summary>
    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
