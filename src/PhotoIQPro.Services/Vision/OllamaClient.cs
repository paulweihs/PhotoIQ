using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoIQPro.Common;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Thin HTTP client for the Ollama REST API (http://localhost:11434).
/// All AI inference stays local — no data leaves the machine.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Vision generation can take well over a minute on CPU-only hardware.
    // Exposed as a constant so callers (e.g. SettingsViewModel test) can reference the same value.
    public const int VisionTimeoutSeconds = 120;

    /// <summary>
    /// Initializes a new instance of the OllamaClient.
    /// </summary>
    /// <remarks>
    /// Defaults to the standard Ollama localhost endpoint (http://127.0.0.1:11434).
    /// HTTP timeout is set to 120 seconds to accommodate slow CPU-only vision analysis.
    /// </remarks>
    /// <param name="baseUrl">The base URL of the Ollama server (e.g., http://localhost:11434 for remote access).</param>
    public OllamaClient(string baseUrl = "http://127.0.0.1:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(VisionTimeoutSeconds) };
    }

    /// <summary>Checks whether Ollama is running and reachable.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns the names of all models currently installed in Ollama.
    /// Returns an empty list if Ollama is unreachable or returns a non-success status.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            var tags = JsonSerializer.Deserialize<TagsResponse>(json, JsonOpts);
            return tags?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch { return []; }
    }

    private record TagsResponse(OllamaModelInfo[]? Models);
    private record OllamaModelInfo(string Name);

    /// <summary>
    /// Calls /api/generate with an optional base64-encoded image (for vision models).
    /// Uses stream=false so the full response is returned in one payload.
    /// </summary>
    public async Task<string> GenerateAsync(
        string model,
        string prompt,
        string? imageBase64,
        CancellationToken ct = default)
    {
        var body = new GenerateRequest(
            model,
            prompt,
            imageBase64 != null ? [imageBase64] : null,
            Stream: false,
            Options: new GenerateOptions(NumPredict: 450, Temperature: 0f, Seed: 42));

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.PostAsync($"{_baseUrl}/api/generate", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            AppLog.Error($"Ollama /api/generate failed: HTTP {(int)resp.StatusCode} — {errorBody}");
            resp.EnsureSuccessStatusCode(); // still throw so callers know the call failed
        }

        var responseJson = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<GenerateResponse>(responseJson, JsonOpts)?.Response
               ?? string.Empty;
    }

    public void Dispose()
    {
        // Cancel any in-flight requests before disposal so the socket is released promptly.
        // Without this, Dispose() blocks until the 120-second vision timeout elapses.
        try { _http.CancelPendingRequests(); } catch { }
        _http.Dispose();
    }

    private record GenerateOptions(
        [property: JsonPropertyName("num_predict")]  int   NumPredict,
        [property: JsonPropertyName("temperature")]  float Temperature,
        [property: JsonPropertyName("seed")]         int   Seed);

    private record GenerateRequest(string Model, string Prompt, string[]? Images, bool Stream, GenerateOptions? Options = null);
    private record GenerateResponse(string Response, bool Done);
}
