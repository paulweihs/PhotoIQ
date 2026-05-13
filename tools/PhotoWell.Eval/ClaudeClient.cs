using System.Text;
using System.Text.Json;

namespace PhotoWell.Eval;

/// <summary>Result of Claude evaluating one app AI description against the actual image.</summary>
public sealed record ClaudeEvalResult(
    bool   Hallucination,   // true = hallucination detected = FAIL for G1
    bool   SubjectMatch,
    bool?  PersonCount,     // null = no people in image (auto-pass)
    bool   Setting,
    bool   KeyObject,
    string Notes
)
{
    // Gate pass/fail — G1 passes when there is NO hallucination
    public bool G1 => !Hallucination;
    public bool G2 => SubjectMatch;
    public bool G3 => PersonCount ?? true;  // no people = auto-pass
    public bool G4 => Setting;
    public bool G5 => KeyObject;

    public int Score =>
        (G1 ? 3 : 0) +
        (G2 ? 2 : 0) +
        (G3 ? 1 : 0) +
        (G4 ? 1 : 0) +
        (G5 ? 2 : 0);
}

/// <summary>
/// Calls the Anthropic Messages API to evaluate an app AI description against the source image.
/// API key is read from the ANTHROPIC_API_KEY environment variable.
/// </summary>
public sealed class ClaudeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _model;

    // Prompt sent to Claude with the image + AiDescription under evaluation.
    private const string PromptTemplate =
        """
        You are evaluating an AI-generated photo description for accuracy.
        Look at the image carefully, then evaluate this description:

        "{description}"

        Score each criterion honestly. Respond with valid JSON only — no markdown, no preamble:
        {
          "hallucination": false,
          "subject_match": true,
          "person_count": true,
          "setting": true,
          "key_object": true,
          "notes": "one sentence explanation of any issues"
        }

        Criteria:
        - hallucination: TRUE if the description mentions anything NOT clearly visible in the image (false is good)
        - subject_match: TRUE if the main subject(s) are correctly identified
        - person_count: TRUE if person count is correct; FALSE if wrong; NULL if there are NO people in the image
        - setting: TRUE if the setting (indoor/outdoor/location type) is correctly described
        - key_object: TRUE if the single most prominent visual element is mentioned
        """;

    public ClaudeClient(string model = "claude-haiku-4-5-20251001")
    {
        _model = model;
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException(
                "ANTHROPIC_API_KEY environment variable is not set.\n" +
                "Set it with: $env:ANTHROPIC_API_KEY = 'sk-ant-...'");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<ClaudeEvalResult?> EvaluateAsync(
        string imagePath,
        string aiDescription,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return null;

        byte[] imageBytes  = await File.ReadAllBytesAsync(imagePath, ct);
        string base64Image = Convert.ToBase64String(imageBytes);
        string mediaType   = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png" : "image/jpeg";

        string prompt = PromptTemplate.Replace("{description}",
            aiDescription.Replace("\\", "\\\\").Replace("\"", "\\\""));

        var body = new
        {
            model      = _model,
            max_tokens = 400,
            messages   = new[]
            {
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new { type = "image",
                              source = new { type = "base64", media_type = mediaType, data = base64Image } },
                        new { type = "text", text = prompt }
                    }
                }
            }
        };

        var request = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response =
            await _http.PostAsync("https://api.anthropic.com/v1/messages", request, ct);

        string raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [ERROR] Claude API {(int)response.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}");
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(raw);
        string text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return ParseResult(text);
    }

    private static ClaudeEvalResult? ParseResult(string text)
    {
        text = text.Trim();

        // Strip markdown fences if Claude wrapped the JSON anyway
        if (text.StartsWith("```"))
        {
            var lines = text.Split('\n');
            text = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```"))).Trim();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            bool hallucination = GetBool(root, "hallucination");
            bool subjectMatch  = GetBool(root, "subject_match");
            bool? personCount  = root.TryGetProperty("person_count", out var pc)
                ? pc.ValueKind == JsonValueKind.Null ? (bool?)null : pc.GetBoolean()
                : null;
            bool setting   = GetBool(root, "setting");
            bool keyObject = GetBool(root, "key_object");
            string notes   = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";

            return new ClaudeEvalResult(hallucination, subjectMatch, personCount, setting, keyObject, notes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Could not parse Claude response ({ex.Message}): " +
                              $"{text[..Math.Min(text.Length, 200)]}");
            return null;
        }
    }

    private static bool GetBool(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
