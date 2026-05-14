using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoWell.Common;

namespace PhotoWell.Services.Vision;

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

    // The num_ctx sent in every /api/generate request. Declared here so
    // OllamaSetupService can compare this against the running model's context size
    // and unload the model if it was loaded with a different (larger) value.
    public const int TargetNumCtx = 1024;

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

    /// <summary>
    /// Creates a named Ollama model from a Modelfile string.
    /// Used to bake parameters (e.g. num_ctx) into a derived model so they are
    /// honoured at load time rather than ignored as per-request options.
    /// Streams the response so it works across all Ollama versions (some ignore stream=false).
    /// </summary>
    public async Task CreateModelAsync(string name, string from, int numCtx, CancellationToken ct = default)
    {
        using var createClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // Ollama v0.5+ uses structured fields (from/parameters) instead of a raw modelfile string.
        var body = JsonSerializer.Serialize(new
        {
            model      = name,
            from,
            parameters = new { num_ctx = numCtx },
            stream     = true,
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await createClient.PostAsync($"{_baseUrl}/api/create", content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            AppLog.Vision($"[OllamaClient] CreateModelAsync '{name}' failed: HTTP {(int)resp.StatusCode} — {errorBody}");
            throw new InvalidOperationException($"Ollama /api/create returned {(int)resp.StatusCode}: {errorBody}");
        }

        // Drain the stream — Ollama sends progress lines ending with {"status":"success"}.
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<CreateEvent>(line, JsonOpts);
            if (evt?.Error != null)
                throw new InvalidOperationException($"Ollama /api/create error: {evt.Error}");
            if (evt?.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                break;
        }
        AppLog.Vision($"[OllamaClient] Created model '{name}'");
    }

    private record CreateEvent(string? Status, string? Error);

    /// <summary>
    /// Unloads the model from VRAM so it reloads fresh on the next inference.
    /// This forces Ollama to honour updated options (e.g. num_ctx) that only
    /// take effect at load time. Fire-and-forget safe — errors are swallowed.
    /// </summary>
    public async Task UnloadModelAsync(string model, CancellationToken ct = default)
    {
        try
        {
            var body = new GenerateRequest(model, "", null, Stream: false, KeepAlive: 0);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_baseUrl}/api/generate", content, ct);
            AppLog.Vision($"[OllamaClient] Unloaded model '{model}' (keep_alive=0): HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            AppLog.Vision($"[OllamaClient] UnloadModelAsync failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the num_ctx the currently-loaded model instance is using, or -1 if
    /// the model is not loaded / unreachable. Ollama exposes this via /api/ps.
    /// </summary>
    public async Task<int> GetRunningContextSizeAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/ps", ct);
            if (!resp.IsSuccessStatusCode) return -1;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var ps   = JsonSerializer.Deserialize<PsResponse>(json, JsonOpts);
            var entry = ps?.Models?.FirstOrDefault(m =>
                m.Name.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));
            return entry?.SizeVram > 0 ? (entry.ContextLength > 0 ? entry.ContextLength : -1) : -1;
        }
        catch (OperationCanceledException) { return -1; }
        catch (Exception ex)
        {
            AppLog.Vision($"[OllamaClient] GetRunningContextSizeAsync failed: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    private record PsResponse(PsModel[]? Models);
    // Ollama /api/ps returns context_length as a top-level field on each model entry.
    private record PsModel(
        string Name,
        long SizeVram,
        [property: JsonPropertyName("context_length")] int ContextLength);

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
            KeepAlive: -1,   // keep model loaded between photos; avoids cold-start penalty
            Options: new GenerateOptions(
                NumPredict: 200,    // 3-4 sentences ≈ 120 tokens; 200 is safe headroom
                Temperature: 0f,
                Seed: 42,
                NumGpu:   99,   // load all layers onto GPU (RTX 4070 has 12 GB VRAM)
                NumBatch: 512,  // larger prompt-encoding batch → faster time-to-first-token
                NumCtx:   1024  // prompt ~100 + image tokens ~200 + output 200 = ~500 max;
                                // 1024 keeps the full KV cache in the ~1 GB VRAM headroom
                                // left after loading the 11 GB model (vs default 4096 which
                                // overflows to system RAM and costs 2-4× in latency)
            ),
            Context: []);   // empty context = fresh KV cache per image; prevents prior image bleeding

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
        [property: JsonPropertyName("seed")]         int   Seed,
        [property: JsonPropertyName("num_gpu")]      int   NumGpu,
        [property: JsonPropertyName("num_batch")]    int   NumBatch,
        [property: JsonPropertyName("num_ctx")]      int   NumCtx);

    private record GenerateRequest(
        string Model, string Prompt, string[]? Images, bool Stream,
        [property: JsonPropertyName("keep_alive")] int KeepAlive = 300,
        GenerateOptions? Options = null,
        // Empty array tells Ollama to start a fresh context for each image — prevents
        // the model from bleeding the previous image's KV cache into the next generation.
        [property: JsonPropertyName("context")] int[] Context = null!);
    private record GenerateResponse(string Response, bool Done);
}
