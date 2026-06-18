using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoWell.Desktop.ViewModels;

namespace PhotoWell.Desktop.Views.Controls;

public partial class ChatPanel : UserControl
{
    public ChatPanel()
    {
        InitializeComponent();
        // ScrollChanged fires after layout has already updated the extent — the most
        // reliable hook for auto-scroll.  ExtentHeightChange > 0 means new content
        // was added; user-initiated scrolling has ExtentHeightChange == 0.
        Scroller.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0)
            Scroller.ScrollToBottom();
    }

    private void FollowUpBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChatViewModel vm)
        {
            if (vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Arrow / Page keys scroll the message history.
        // The input box is single-line so these keys have no native action there.
        switch (e.Key)
        {
            case Key.Up:       Scroller.LineUp();   e.Handled = true; break;
            case Key.Down:     Scroller.LineDown();  e.Handled = true; break;
            case Key.PageUp:   Scroller.PageUp();    e.Handled = true; break;
            case Key.PageDown: Scroller.PageDown();  e.Handled = true; break;
        }
    }

}

// DataTemplateSelector for user vs assistant messages
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate      { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ChatMessageViewModel m
            ? (m.IsUser ? UserTemplate : AssistantTemplate)
            : base.SelectTemplate(item, container);
}
