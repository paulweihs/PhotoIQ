using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Verifies Ollama is running locally and the required model is present on disk.
///
/// Privacy guarantee: makes ONLY localhost HTTP calls (127.0.0.1:11434).
/// Never contacts external servers. Never downloads anything.
/// Ollama and the model are installed by the PhotoIQ installer — if either is
/// missing this service reports an error and instructs the user to reinstall.
/// </summary>
public sealed class OllamaSetupService : IOllamaSetupService, IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Common Ollama install locations on Windows (populated by the installer).
    private static readonly string[] OllamaExeCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", "Ollama", "ollama.exe"),
        @"C:\Program Files\Ollama\ollama.exe",
    ];

    public OllamaSetupService(string baseUrl = "http://127.0.0.1:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async IAsyncEnumerable<OllamaSetupProgress> EnsureReadyAsync(
        string modelName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new OllamaSetupProgress(OllamaSetupStage.Checking, "Checking AI engine…");

        bool up = await IsOllamaReachableAsync(ct);

        if (!up)
        {
            yield return new OllamaSetupProgress(OllamaSetupStage.StartingOllama, "Starting AI engine…");
            up = await TryStartLocalOllamaAsync(ct);
        }

        if (!up)
        {
            yield return new OllamaSetupProgress(
                OllamaSetupStage.Error,
                "Ollama is not running and could not be started.\nPlease reinstall PhotoIQ Pro.");
            yield break;
        }

        bool modelPresent = await IsModelPresentAsync(modelName, ct);

        if (!modelPresent)
        {
            yield return new OllamaSetupProgress(
                OllamaSetupStage.Error,
                $"AI model \"{modelName}\" was not found.\nPlease reinstall PhotoIQ Pro.");
            yield break;
        }

        yield return new OllamaSetupProgress(OllamaSetupStage.Ready, "AI engine ready");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<bool> IsOllamaReachableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> IsModelPresentAsync(string modelName, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}/api/tags", ct);
            var tags = JsonSerializer.Deserialize<TagsResponse>(json, JsonOpts);
            return tags?.Models?.Any(m =>
                m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(modelName + ":", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex)
        {
            // Log so operators can distinguish a network/parse failure from a genuinely
            // missing model — both surface as "model not found" to the user but the root
            // cause is different.
            AppLog.Error($"IsModelPresentAsync failed (model='{modelName}'): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the locally-installed Ollama process. Does not contact the internet.
    /// Polls localhost until Ollama answers or the timeout expires.
    /// </summary>
    private async Task<bool> TryStartLocalOllamaAsync(CancellationToken ct)
    {
        var exe = OllamaExeCandidates.FirstOrDefault(File.Exists);
        if (exe == null)
        {
            // Try "ollama" from PATH (installer may have added it).
            exe = "ollama";
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, "serve")
            {
                CreateNoWindow  = true,
                UseShellExecute = false,
            });
        }
        catch { return false; }

        // Poll localhost — up to 15 seconds.
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await IsOllamaReachableAsync(ct)) return true;
        }

        return false;
    }

    public void Dispose() => _http.Dispose();

    private record TagsResponse(List<ModelEntry>? Models);
    private record ModelEntry(string Name);
}
