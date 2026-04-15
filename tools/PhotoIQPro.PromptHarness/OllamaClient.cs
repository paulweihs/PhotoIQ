using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoIQPro.PromptHarness;

/// <summary>
/// Minimal async HTTP client for the Ollama local inference server.
/// Sends a vision prompt with one image and returns the model's response text.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout     = TimeSpan.FromMinutes(3)   // vision models can be slow
        };
    }

    /// <summary>
    /// Returns the model's description of the image, or null on any error.
    /// </summary>
    public async Task<string?> GenerateAsync(
        string model,
        string prompt,
        string imagePath,
        CancellationToken ct = default)
    {
        try
        {
            byte[] bytes  = await File.ReadAllBytesAsync(imagePath, ct);
            string b64    = Convert.ToBase64String(bytes);

            var body = new GenerateRequest
            {
                Model  = model,
                Prompt = prompt,
                Images = [b64],
                Stream = false,
                Options = new GenerateOptions { Temperature = 0, Seed = 42, NumPredict = 300 }
            };

            using var resp = await _http.PostAsJsonAsync("api/generate", body, ct);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("response", out var field))
                return field.GetString();

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Ollama] Error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> IsModelAvailableAsync(string model, CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync("api/tags", ct);
            return json.Contains(model, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
    }

    private sealed class GenerateRequest
    {
        [JsonPropertyName("model")]   public string          Model   { get; init; } = "";
        [JsonPropertyName("prompt")]  public string          Prompt  { get; init; } = "";
        [JsonPropertyName("images")]  public string[]        Images  { get; init; } = [];
        [JsonPropertyName("stream")]  public bool            Stream  { get; init; }
        [JsonPropertyName("options")] public GenerateOptions Options { get; init; } = new();
    }

    private sealed class GenerateOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("seed")]        public int    Seed        { get; init; }
        [JsonPropertyName("num_predict")] public int    NumPredict  { get; init; }
    }
}
