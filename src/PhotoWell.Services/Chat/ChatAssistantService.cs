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
            "Optionally also filters by a content query (e.g. 'candle', 'beach', 'birthday cake') " +
            "so you can find photos of a person in a specific scene or context. " +
            "Includes confirmed face tags and high-confidence (≥0.7) unconfirmed matches for a wider net.",
            new {
                type = "object",
                properties = new {
                    name = new { type = "string", description = "The person's name" },
                    include_unconfirmed = new {
                        type = "boolean",
                        description = "Whether to include high-confidence unconfirmed face matches (default true)"
                    },
                    query = new {
                        type = "string",
                        description = "Optional content/scene query to narrow results (e.g. 'candle', 'beach'). " +
                                      "Use this instead of calling search_photos separately."
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

        Tool("export_photos",
            "Copy photos to a folder on disk. A folder-picker dialog opens so the user chooses the " +
            "destination themselves. If person_name is given, exports that person's photos; otherwise " +
            "exports the photos currently shown in the gallery. Originals are never moved or modified.",
            new {
                type = "object",
                properties = new {
                    person_name = new {
                        type = "string",
                        description = "Limit the export to photos of this person (optional)"
                    },
                    confirmed_only = new {
                        type = "boolean",
                        description = "True (default) = only verified/confirmed face matches; " +
                                      "false = also include high-confidence unconfirmed matches"
                    }
                },
                required = Array.Empty<string>()
            }),

        Tool("email_photos",
            "Open the user's default email client with photos attached as a new draft message. " +
            "The user addresses and sends the email themselves. If person_name is given, attaches " +
            "that person's photos; if filename is given (or the user says \"this photo\"), attaches " +
            "that photo or the currently selected photos; otherwise attaches the photos currently " +
            "shown in the gallery. Limited to 20 attachments — suggest export_photos for larger sets.",
            new {
                type = "object",
                properties = new {
                    person_name = new {
                        type = "string",
                        description = "Attach only photos of this person (optional)"
                    },
                    confirmed_only = new {
                        type = "boolean",
                        description = "True (default) = only verified/confirmed face matches; " +
                                      "false = also include high-confidence unconfirmed matches"
                    },
                    filename = new {
                        type = "string",
                        description = "Attach one specific photo by filename (optional)"
                    }
                },
                required = Array.Empty<string>()
            }),

        Tool("print_photo",
            "Open the Windows print dialog for photos. Uses the currently selected photo(s) " +
            "when filename is omitted (max 5 at a time — one dialog opens per photo).",
            new {
                type = "object",
                properties = new {
                    filename = new { type = "string", description = "The photo's filename (optional)" }
                },
                required = Array.Empty<string>()
            }),

        Tool("set_wallpaper",
            "Set a photo as the Windows desktop wallpaper. Uses the currently selected photo " +
            "when filename is omitted.",
            new {
                type = "object",
                properties = new {
                    filename = new { type = "string", description = "The photo's filename (optional)" }
                },
                required = Array.Empty<string>()
            }),

        Tool("open_in_editor",
            "Open photos in one of the user's configured external photo editors (e.g. Photoshop, " +
            "GIMP). Uses the currently selected photo(s) when filename is omitted (max 10).",
            new {
                type = "object",
                properties = new {
                    editor_name = new {
                        type = "string",
                        description = "Name of the configured editor (optional when only one is configured)"
                    },
                    filename = new { type = "string", description = "The photo's filename (optional)" }
                },
                required = Array.Empty<string>()
            }),
    ];

    private static ChatToolDefinition Tool(string name, string description, object parameters) =>
        new(name, description, parameters);

    private const string SystemPrompt = """
        You are the AI assistant built into PhotoWell, a local photo library app.
        You have DIRECT access to the user's photo library through your tools: you can
        search it, filter it, open photos, show people, export copies of photos to a
        folder, attach photos to a new email, print, set the desktop wallpaper, and
        open a photo in the user's external editor. Never tell the user you don't have
        access to their library, and never ask them to call a function or show them
        function syntax — you call the tools yourself and report what happened.

        Your tools:
        - search_photos: full-text / semantic search
        - filter_by_person: show photos of a named person (uses face recognition — not just text tags)
        - filter_by_date: filter by date range
        - clear_filters: reset all active filters
        - get_library_stats: counts, date range, people
        - open_photo: open a photo by filename in the viewer
        - show_people: open the People window
        - export_photos: copy photos (optionally of one person) to a folder the user picks in a dialog
        - email_photos: open a draft email with photos attached (max 20); the user addresses and sends it
        - print_photo: open the print dialog (selected photo(s) when no filename given)
        - set_wallpaper: set a photo as the desktop wallpaper (selected photo when no filename given)
        - open_in_editor: open photo(s) in a configured external editor like Photoshop

        Guidelines:
        - When the user asks you to show, find, filter, open, export, email, print, or
          edit photos — even when phrased as "how do I…" or "can you show me how…" —
          call the matching tool and do it, then summarise the outcome in 1-2 sentences.
        - "This photo", "these photos", or "the selected photos" means the user's current
          selection — call the tool without a filename; all selected photos are used.
        - For person queries ("show me photos of Sarah"), use filter_by_person with
          include_unconfirmed=true. If the user says "verified" or "confirmed", use
          include_unconfirmed=false (or confirmed_only=true when exporting).
        - For person + scene/content queries ("photos of Jacob with a candle",
          "Sarah at the beach"), use filter_by_person with the query parameter set
          to the scene/content part. Do NOT call search_photos separately — it would
          replace the person filter.
        - Only answer in prose when no tool covers the request; then briefly say what you
          CAN do instead.
        - Never mention tool or function names to the user — describe actions in plain words.
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

        // Some models/Ollama versions deliver arguments as a JSON string rather
        // than an object — unwrap it so the property getters work either way.
        if (args.ValueKind == JsonValueKind.String)
        {
            try { args = JsonDocument.Parse(args.GetString() ?? "{}").RootElement.Clone(); }
            catch (JsonException) { /* getters fall back to defaults below */ }
        }

        try
        {
            return fn switch
            {
                "search_photos"   => await _actions.ChatSearchAsync(GetString(args, "query")),
                "filter_by_person" => await _actions.ChatFilterByPersonAsync(
                                        GetString(args, "name"),
                                        GetBool(args, "include_unconfirmed", defaultValue: true),
                                        GetStringOpt(args, "query")),
                "filter_by_date"  => await _actions.ChatFilterByDateAsync(
                                        GetStringOpt(args, "start_date"),
                                        GetStringOpt(args, "end_date")),
                "clear_filters"   => await _actions.ChatClearFiltersAsync(),
                "get_library_stats" => await _actions.ChatGetLibraryStatsAsync(),
                "open_photo"      => await _actions.ChatOpenPhotoAsync(GetString(args, "filename")),
                "show_people"     => await _actions.ChatShowPeopleAsync(),
                "export_photos"   => await _actions.ChatExportPhotosAsync(
                                        GetStringOpt(args, "person_name"),
                                        GetBool(args, "confirmed_only", defaultValue: true)),
                "email_photos"    => await _actions.ChatEmailPhotosAsync(
                                        GetStringOpt(args, "person_name"),
                                        GetBool(args, "confirmed_only", defaultValue: true),
                                        GetStringOpt(args, "filename")),
                "print_photo"     => await _actions.ChatPrintPhotoAsync(GetStringOpt(args, "filename")),
                "set_wallpaper"   => await _actions.ChatSetWallpaperAsync(GetStringOpt(args, "filename")),
                "open_in_editor"  => await _actions.ChatOpenInEditorAsync(
                                        GetStringOpt(args, "editor_name"),
                                        GetStringOpt(args, "filename")),
                _                 => $"Unknown tool: {fn}"
            };
        }
        catch (Exception ex)
        {
            return $"Tool '{fn}' failed: {ex.Message}";
        }
    }

    // Small local models frequently get JSON types wrong (booleans as "true",
    // numbers as strings) — be lenient instead of throwing back a tool error.

    internal static string GetString(JsonElement args, string key) =>
        GetStringOpt(args, key) ?? "";

    internal static string? GetStringOpt(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null   => null,
            _                    => v.ToString()
        };
    }

    internal static bool GetBool(JsonElement args, string key, bool defaultValue)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v))
            return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : defaultValue,
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d != 0 : defaultValue,
            _                    => defaultValue
        };
    }
}
