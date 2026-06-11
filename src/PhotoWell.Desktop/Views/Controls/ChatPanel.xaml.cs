using System.Collections.Specialized;
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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ChatViewModel oldVm)
            oldVm.Messages.CollectionChanged -= Messages_CollectionChanged;
        if (e.NewValue is ChatViewModel vm)
            vm.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    private void FollowUpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChatViewModel vm)
        {
            if (vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Auto-scroll to bottom when a new message arrives
        Scroller.ScrollToBottom();
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
