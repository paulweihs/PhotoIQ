using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoIQPro.Desktop.Views;

/// <summary>
/// Shared TextBox event-handler logic used by multiple windows.
/// Centralises the two recurring patterns so changes propagate everywhere.
/// </summary>
internal static class TextBoxHelpers
{
    /// <summary>
    /// Scrolls the TextBox back to the start after a text change, keeping the filename
    /// visible rather than the middle of a long path.
    /// </summary>
    internal static void ScrollToHome(object sender, TextChangedEventArgs _)
    {
        if (sender is TextBox tb) { tb.CaretIndex = 0; tb.ScrollToHome(); }
    }

    /// <summary>
    /// On first focus (PreviewMouseDown while not yet focused): selects all text and
    /// suppresses the click so WPF does not immediately deselect it.
    /// Subsequent clicks inside an already-focused box are passed through normally,
    /// allowing cursor placement and partial selection.
    /// </summary>
    internal static void SelectAllOnFirstFocus(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsFocused)
        {
            tb.Focus();
            tb.SelectAll();
            e.Handled = true;
        }
    }
}
