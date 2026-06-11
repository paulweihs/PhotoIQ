using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Desktop.ViewModels;

public sealed class ChatMessageViewModel
{
    public string Text    { get; }
    public bool   IsUser  { get; }
    public ChatMessageViewModel(string text, bool isUser) { Text = text; IsUser = isUser; }
}

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly IChatAssistantService _assistant;

    [ObservableProperty] private string  _inputText    = "";
    [ObservableProperty] private bool    _isThinking   = false;
    [ObservableProperty] private string  _thinkingText = "Thinking…";
    [ObservableProperty] private bool    _isPanelOpen  = false;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public ChatViewModel(IChatAssistantService assistant)
    {
        _assistant = assistant;
    }

    [RelayCommand]
    public void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    public void ClearHistory()
    {
        _assistant.ClearHistory();
        Messages.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputText = "";
        IsPanelOpen = true;
        Messages.Add(new ChatMessageViewModel(text, isUser: true));

        IsThinking = true;
        ThinkingText = "Thinking…";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var reply = await _assistant.SendAsync(
            text,
            onProgress: p => Application.Current.Dispatcher.Invoke(() => ThinkingText = p),
            ct: cts.Token);

        IsThinking = false;

        if (!string.IsNullOrWhiteSpace(reply))
            Messages.Add(new ChatMessageViewModel(reply, isUser: false));
    }

    private bool CanSend() => !IsThinking;

    // Called from MainWindow when user presses Ctrl+Enter or clicks "Ask AI"
    public void SubmitQuery(string query)
    {
        if (UserPreferences.Current.IsExpressMode)
        {
            IsPanelOpen = true;
            Messages.Add(new ChatMessageViewModel(query, isUser: true));
            Messages.Add(new ChatMessageViewModel(
                "AI chat is a Standard feature. Upgrade to Standard to ask questions about your library, filter by person, and more.",
                isUser: false));
            return;
        }
        InputText = query;
        SendCommand.Execute(null);
    }
}
