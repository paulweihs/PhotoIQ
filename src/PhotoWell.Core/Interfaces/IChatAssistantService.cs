namespace PhotoWell.Core.Interfaces;

public interface IChatAssistantService
{
    /// <summary>Sends a user message and returns the assistant's final text reply after all tool calls complete.</summary>
    Task<string> SendAsync(string userMessage, Action<string>? onProgress = null, CancellationToken ct = default);

    /// <summary>Wires up the action dispatcher. Must be called once after the main window is ready.</summary>
    void SetActions(IAssistantActions actions);

    /// <summary>Clears the conversation history.</summary>
    void ClearHistory();

    bool IsAvailable { get; }
}
