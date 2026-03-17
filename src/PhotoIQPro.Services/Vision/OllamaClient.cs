using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Checks whether Ollama is running and reachable.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

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
            Stream: false);

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await _http.PostAsync($"{_baseUrl}/api/generate", content, ct);
        resp.EnsureSuccessStatusCode();

        var responseJson = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<GenerateResponse>(responseJson, JsonOpts)?.Response
               ?? string.Empty;
    }

    public void Dispose() => _http.Dispose();

    private record GenerateRequest(string Model, string Prompt, string[]? Images, bool Stream);
    private record GenerateResponse(string Response, bool Done);
}
