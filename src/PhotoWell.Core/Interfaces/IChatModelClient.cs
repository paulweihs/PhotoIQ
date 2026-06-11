using System.Text.Json;

namespace PhotoWell.Core.Interfaces;

/// <summary>
/// A chat turn in a provider-neutral shape. Role is "system", "user", "assistant", or "tool".
/// </summary>
public record ChatMessage(string Role, string? Content, IReadOnlyList<ChatToolCall>? ToolCalls = null);

/// <summary>A tool invocation requested by the model.</summary>
public record ChatToolCall(string Name, JsonElement Arguments);

/// <summary>A tool the model may call. Parameters is a JSON-schema-shaped object.</summary>
public record ChatToolDefinition(string Name, string Description, object Parameters);

/// <summary>
/// Multi-turn chat completion with optional tool calling, independent of any
/// specific LLM backend. Implemented by OllamaClient today; any future backend
/// (llama.cpp, LM Studio, a different runtime) plugs in behind this interface.
/// </summary>
public interface IChatModelClient
{
    Task<ChatMessage> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools = null,
        CancellationToken ct = default);
}
