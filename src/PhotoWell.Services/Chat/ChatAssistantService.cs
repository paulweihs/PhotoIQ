using System.Text.Json;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Services.Chat;

/// <summary>
/// Local AI chat assistant backed by any IChatModelClient (Ollama today) with tool calling.
/// Maintains multi-turn conversation history and dispatches tool calls to IAssistantActions.
/// All inference is local — no data leaves the machine.
/// </summary>
public sealed class ChatAssistantService : IChatAssistantService
{
    private readonly IChatModelClient _chatClient;
    private readonly List<ChatMessage> _history = [];
    private IAssistantActions? _actions;

    public bool IsAvailable => _actions != null;

    private static readonly IReadOnlyList<ChatToolDefinition> Tools =
    [
        Tool("search_photos",
            "Search photos by natural-language query, tags, or keywords. Returns a summary of matching photos.",
            new {
                type = "object",
                properties = new {
                    query = new { type = "string", description = "The search query" }
                },
                required = new[] { "query" }
            }),

        Tool("filter_by_person",
            "Show photos containing a specific person (identified by face recognition). " +
            "Includes confirmed face tags and high-confidence (≥0.7) unconfirmed matches for a wider net.",
            new {
                type = "object",
                properties = new {
                    name = new { type = "string", description = "The person's name" },
                    include_unconfirmed = new {
                        type = "boolean",
                        description = "Whether to include high-confidence unconfirmed face matches (default true)"
                    }
                },
                required = new[] { "name" }
            }),

        Tool("filter_by_date",
            "Filter the photo gallery to a date range. Either or both dates may be omitted.",
            new {
                type = "object",
                properties = new {
                    start_date = new { type = "string", description = "ISO-8601 start date, e.g. 2024-01-01 (optional)" },
                    end_date   = new { type = "string", description = "ISO-8601 end date, e.g. 2024-12-31 (optional)" }
                },
                required = Array.Empty<string>()
            }),

        Tool("clear_filters",
            "Remove all active search/filter criteria and show the full library.",
            new {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }),

        Tool("get_library_stats",
            "Return statistics about the photo library: total count, date range, people count, etc.",
            new {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }),

        Tool("open_photo",
            "Open a specific photo in the full-screen viewer by filename.",
            new {
                type = "object",
                properties = new {
                    filename = new { type = "string", description = "The filename (not full path) of the photo to open" }
                },
                required = new[] { "filename" }
            }),

        Tool("show_people",
            "Open the People window to show all recognised people in the library.",
            new {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }),
    ];

    private static ChatToolDefinition Tool(string name, string description, object parameters) =>
        new(name, description, parameters);

    private const string SystemPrompt = """
        You are a helpful photo-library assistant built into PhotoWell.
        You help users search, filter, and understand their photo collection.

        You have access to the following actions:
        - search_photos: full-text / semantic search
        - filter_by_person: show photos of a named person (uses face recognition — not just text tags)
        - filter_by_date: filter by date range
        - clear_filters: reset all active filters
        - get_library_stats: counts, date range, people
        - open_photo: open a photo by filename in the viewer
        - show_people: open the People window

        Guidelines:
        - When a user asks to "show" or "find" photos, call the appropriate filter/search tool first, then respond.
        - For person queries ("show me photos of Sarah"), use filter_by_person with include_unconfirmed=true.
        - After calling a tool, summarise the result in 1-2 sentences and ask if there's anything else.
        - For how-to questions, answer directly without calling a tool.
        - Be concise — users are looking at their photos, not reading an essay.
        - Never invent photo filenames or fabricate library data; only describe what the tools return.
        """;

    public ChatAssistantService(IChatModelClient chatClient)
    {
        _chatClient = chatClient;
    }

    public void SetActions(IAssistantActions actions) => _actions = actions;

    public void ClearHistory() => _history.Clear();

    public async Task<string> SendAsync(
        string userMessage,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_actions == null)
            return "Chat assistant is not available yet — please wait for the main window to finish loading.";

        var model = UserPreferences.Current.ChatModelName;

        // Build message list: system + history + new user message
        var messages = new List<ChatMessage>();
        if (_history.Count == 0)
            messages.Add(new ChatMessage("system", SystemPrompt + "\n\n" + _actions.GetCurrentContext()));
        else
            messages.Add(new ChatMessage("system", SystemPrompt));

        messages.AddRange(_history);
        messages.Add(new ChatMessage("user", userMessage));

        _history.Add(new ChatMessage("user", userMessage));

        try
        {
            // Agentic loop: keep calling until the model returns plain text (no more tool_calls)
            const int maxIterations = 6;
            for (int i = 0; i < maxIterations; i++)
            {
                onProgress?.Invoke(i == 0 ? "Thinking…" : "Working…");

                var reply = await _chatClient.ChatAsync(model, messages, Tools, ct);

                if (reply.ToolCalls is { Count: > 0 } toolCalls)
                {
                    // Append assistant turn with tool calls to the running messages list
                    messages.Add(reply);

                    // Execute each tool call and append results
                    foreach (var tc in toolCalls)
                    {
                        var result = await DispatchToolAsync(tc, ct);
                        AppLog.Vision($"[ChatAssistant] Tool '{tc.Name}' → {result[..Math.Min(120, result.Length)]}");
                        messages.Add(new ChatMessage("tool", result));
                    }
                    // Loop: send results back so model can produce final text
                    continue;
                }

                // No tool calls — this is the final text response
                var text = reply.Content ?? string.Empty;
                _history.Add(reply);
                return text;
            }

            // Safety net: too many iterations
            return "I ran into a loop trying to answer that. Please try rephrasing your question.";
        }
        catch (OperationCanceledException)
        {
            _history.RemoveAt(_history.Count - 1); // remove the user message we added
            return string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[ChatAssistant] SendAsync failed: {ex.GetType().Name}: {ex.Message}");
            _history.RemoveAt(_history.Count - 1);
            return $"Sorry, I couldn't reach the AI model. Make sure Ollama is running with the '{UserPreferences.Current.ChatModelName}' model installed.";
        }
    }

    private async Task<string> DispatchToolAsync(ChatToolCall tc, CancellationToken ct)
    {
        if (_actions == null) return "Error: actions not wired up.";

        var fn   = tc.Name;
        var args = tc.Arguments;

        try
        {
            return fn switch
            {
                "search_photos"   => await _actions.ChatSearchAsync(GetString(args, "query")),
                "filter_by_person" => await _actions.ChatFilterByPersonAsync(
                                        GetString(args, "name"),
                                        GetBool(args, "include_unconfirmed", defaultValue: true)),
                "filter_by_date"  => await _actions.ChatFilterByDateAsync(
                                        GetStringOpt(args, "start_date"),
                                        GetStringOpt(args, "end_date")),
                "clear_filters"   => await _actions.ChatClearFiltersAsync(),
                "get_library_stats" => await _actions.ChatGetLibraryStatsAsync(),
                "open_photo"      => await _actions.ChatOpenPhotoAsync(GetString(args, "filename")),
                "show_people"     => await _actions.ChatShowPeopleAsync(),
                _                 => $"Unknown tool: {fn}"
            };
        }
        catch (Exception ex)
        {
            return $"Tool '{fn}' failed: {ex.Message}";
        }
    }

    private static string GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    private static string? GetStringOpt(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) ? v.GetString() : null;

    private static bool GetBool(JsonElement args, string key, bool defaultValue) =>
        args.TryGetProperty(key, out var v) ? v.GetBoolean() : defaultValue;
}
