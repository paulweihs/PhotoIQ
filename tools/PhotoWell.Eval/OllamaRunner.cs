using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoWell.Eval;

/// <summary>
/// Minimal async HTTP client for the Ollama local inference server.
/// Sends a single vision prompt with one image and returns the model's response text.
/// </summary>
public sealed class OllamaRunner : IDisposable
{
    private const string BaseUrl = "http://localhost:11434";

    private readonly HttpClient _http;
    private bool _disposed;

    public OllamaRunner()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            // Vision models can be slow on first token — allow 3 minutes per call.
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    /// <summary>
    /// Sends <paramref name="prompt"/> and the image at <paramref name="imagePath"/>
    /// to the specified Ollama <paramref name="model"/> and returns the response text.
    /// Returns an empty string on any error so the caller can treat it as a failed run.
    /// </summary>
    public async Task<string> GenerateAsync(string model, string prompt, string imagePath)
    {
        try
        {
            // Read image and encode to base64 for the Ollama multimodal API.
            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
            string base64Image = Convert.ToBase64String(imageBytes);

            var requestBody = new OllamaGenerateRequest
            {
                Model = model,
                Prompt = prompt,
                Images = [base64Image],
                Stream = false,
                Options = new OllamaOptions
                {
                    Temperature = 0,
                    Seed = 42,
                    NumPredict = 300
                }
            };

            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                "/api/generate", requestBody);

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("response", out JsonElement responseField))
                return responseField.GetString() ?? string.Empty;

            return string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [OllamaRunner] Error calling model '{model}': {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Checks whether a model is available in the local Ollama instance
    /// by querying /api/tags and looking for the model name in the response text.
    /// </summary>
    public async Task<bool> IsModelAvailableAsync(string model)
    {
        try
        {
            string json = await _http.GetStringAsync("/api/tags");
            // Simple substring check — the model name will appear in the JSON if installed.
            return json.Contains(model, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
    }

    // ── Request / response DTOs ────────────────────────────────────────────────

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = string.Empty;

        [JsonPropertyName("images")]
        public string[] Images { get; init; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("options")]
        public OllamaOptions Options { get; init; } = new();
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("seed")]
        public int Seed { get; init; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; init; }
    }
}
