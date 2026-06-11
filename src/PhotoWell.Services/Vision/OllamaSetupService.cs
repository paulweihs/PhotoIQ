using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Services.Vision;

/// <summary>
/// Ensures Ollama is installed, running, and the required vision model is present.
///
/// Flow:
///   1. Is Ollama reachable?  → yes → is model present? → yes → Ready
///   2. Ollama not reachable  → try to start it from known install paths
///   3. Still not reachable   → is Ollama installed at all?
///                              → yes (offline machine) → start it, report error if stuck
///                              → no  → download installer from ollama.com → install silently
///   4. Model missing         → ollama pull <model> with streaming progress
///
/// Privacy guarantee: only contacts 127.0.0.1 and ollama.com (installer + model pull).
/// All calls to ollama.com only happen when Ollama/model is genuinely missing.
/// </summary>
public sealed class OllamaSetupService : IOllamaSetupService, IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly OllamaClient? _ollamaClient;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string OllamaInstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    private static readonly string[] OllamaExeCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Programs", "Ollama", "ollama.exe"),
        @"C:\Program Files\Ollama\ollama.exe",
    ];

    public OllamaSetupService(string baseUrl = "http://127.0.0.1:11434", OllamaClient? ollamaClient = null)
    {
        _baseUrl      = baseUrl.TrimEnd('/');
        _http         = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ollamaClient = ollamaClient;
    }

    public async IAsyncEnumerable<OllamaSetupProgress> EnsureReadyAsync(
        string modelName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Use a channel so helper methods can yield progress without the
        // try/catch yield restrictions in C# iterator methods.
        var channel = Channel.CreateUnbounded<OllamaSetupProgress>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var task = RunSetupAsync(channel.Writer, modelName, ct);

        await foreach (var p in channel.Reader.ReadAllAsync(ct))
            yield return p;

        await task; // propagate any unhandled exception
    }

    private async Task RunSetupAsync(
        ChannelWriter<OllamaSetupProgress> out_,
        string modelName,
        CancellationToken ct)
    {
        try
        {
            await out_.WriteAsync(new(OllamaSetupStage.Checking, "Checking AI engine…"), ct);

            // ── Step 1: Get Ollama running ─────────────────────────────────────
            bool up = await IsOllamaReachableAsync(ct);

            if (!up)
            {
                string? exePath = OllamaExeCandidates.FirstOrDefault(File.Exists)
                               ?? FindOllamaOnPath();

                if (exePath != null)
                {
                    await out_.WriteAsync(new(OllamaSetupStage.StartingOllama, "Starting AI engine…"), ct);
                    up = await TryStartOllamaAsync(exePath, ct);
                }
            }

            if (!up)
            {
                bool installed = await DownloadAndInstallOllamaAsync(out_, ct);
                if (!installed) return;

                await out_.WriteAsync(new(OllamaSetupStage.StartingOllama, "Starting AI engine…"), ct);
                string? exePath = OllamaExeCandidates.FirstOrDefault(File.Exists)
                               ?? FindOllamaOnPath();
                if (exePath != null)
                    up = await TryStartOllamaAsync(exePath, ct);

                if (!up)
                {
                    await out_.WriteAsync(new(OllamaSetupStage.Error,
                        "Ollama was installed but could not be started. Please restart PhotoWell."), ct);
                    return;
                }
            }

            // ── Step 2: Ensure base model is present ──────────────────────────
            // modelName is the derived model (e.g. "photowell-minicpm-v"); the base
            // model (e.g. "minicpm-v") is what Ollama pulls from the registry.
            string baseModelName = PhotoWell.Common.AppSettings.VisionBaseModelName;
            bool basePresent = await IsModelPresentAsync(baseModelName, ct);
            if (!basePresent)
            {
                bool pulled = await PullModelAsync(out_, baseModelName, ct);
                if (!pulled) return;
            }

            // ── Step 3: Ensure derived model exists with num_ctx baked in ─────
            // Ollama ignores per-request num_ctx when the model is already loaded.
            // Creating a derived model with PARAMETER num_ctx 1024 in its Modelfile
            // forces Ollama to always load it at that context size, keeping the KV
            // cache in VRAM and hitting 5–7 s/photo (vs 47–125 s at the 2048 default).
            bool derivedPresent = await IsModelPresentAsync(modelName, ct);
            if (!derivedPresent && _ollamaClient != null)
            {
                await out_.WriteAsync(new(OllamaSetupStage.Checking, "Configuring AI model…"), ct);
                AppLog.Vision($"[OllamaSetup] Creating derived model '{modelName}' from '{baseModelName}' with num_ctx={OllamaClient.TargetNumCtx}");
                await _ollamaClient.CreateModelAsync(modelName, baseModelName, OllamaClient.TargetNumCtx, ct);
            }

            await out_.WriteAsync(new(OllamaSetupStage.Ready, "AI engine ready"), ct);
        }
        finally
        {
            out_.Complete();
        }
    }

    // ── Chat model silent pull ────────────────────────────────────────────────

    public async Task EnsureChatModelAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            if (await IsModelPresentAsync(modelName, ct)) return;

            AppLog.Vision($"[OllamaSetup] Chat model '{modelName}' not found — pulling silently…");
            using var pullClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(_baseUrl) };
            var body = JsonSerializer.Serialize(new { name = modelName, stream = true });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await pullClient.PostAsync("/api/pull", content, ct);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var evt = JsonSerializer.Deserialize<PullEvent>(line, JsonOpts);
                if (evt?.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                {
                    AppLog.Vision($"[OllamaSetup] Chat model '{modelName}' pull complete.");
                    break;
                }
                if (evt?.Error != null)
                {
                    AppLog.Error($"[OllamaSetup] Chat model pull error: {evt.Error}");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error($"[OllamaSetup] EnsureChatModelAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Ollama download & install ─────────────────────────────────────────────

    /// <returns>True if install succeeded, false if an error was written.</returns>
    private async Task<bool> DownloadAndInstallOllamaAsync(
        ChannelWriter<OllamaSetupProgress> out_,
        CancellationToken ct)
    {
        var installerPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

        await out_.WriteAsync(new(OllamaSetupStage.DownloadingOllama, "Downloading AI engine…"), ct);

        using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Exception? downloadEx = null;
        try
        {
            using var response = await downloadClient.GetAsync(
                OllamaInstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total    = response.Content.Headers.ContentLength ?? -1;
            long received = 0;

            await using var netStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(installerPath);
            var buffer = new byte[81920];
            int read;

            while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                string sizeText = total > 0
                    ? $"{FormatBytes(received)} / {FormatBytes(total)}"
                    : FormatBytes(received);
                await out_.WriteAsync(new(
                    OllamaSetupStage.DownloadingOllama,
                    $"Downloading AI engine… {sizeText}",
                    received, total), ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { downloadEx = ex; }

        if (downloadEx != null)
        {
            await out_.WriteAsync(new(OllamaSetupStage.Error,
                $"Failed to download AI engine: {downloadEx.Message}\nCheck your internet connection and retry."), ct);
            return false;
        }

        // Verify the download is genuinely signed by Ollama before running it elevated.
        if (!Security.AuthenticodeVerifier.IsSignedBy(installerPath, "Ollama"))
        {
            try { File.Delete(installerPath); } catch { }
            await out_.WriteAsync(new(OllamaSetupStage.Error,
                "The downloaded AI engine installer failed signature verification and was discarded.\n" +
                "Please retry, or install Ollama manually from https://ollama.com."), ct);
            return false;
        }

        // Install silently.
        await out_.WriteAsync(new(OllamaSetupStage.InstallingOllama, "Installing AI engine…"), ct);

        Exception? installEx = null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(installerPath, "/S")
            {
                UseShellExecute = true,
                Verb            = "runas",
            });
            if (proc != null)
                await proc.WaitForExitAsync(ct);

            await Task.Delay(2000, ct); // allow PATH registration to settle
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { installEx = ex; }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }

        if (installEx != null)
        {
            await out_.WriteAsync(new(OllamaSetupStage.Error,
                $"Failed to install AI engine: {installEx.Message}"), ct);
            return false;
        }

        return true;
    }

    // ── Model pull ────────────────────────────────────────────────────────────

    /// <returns>True if pull succeeded, false if an error was written.</returns>
    private async Task<bool> PullModelAsync(
        ChannelWriter<OllamaSetupProgress> out_,
        string modelName,
        CancellationToken ct)
    {
        await out_.WriteAsync(new(OllamaSetupStage.PullingModel,
            "Downloading AI vision model… (one-time download, ~4–5 GB)"), ct);

        using var pullClient = new HttpClient
        {
            Timeout     = Timeout.InfiniteTimeSpan,
            BaseAddress = new Uri(_baseUrl),
        };

        var body = JsonSerializer.Serialize(new { name = modelName, stream = true });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        Exception? startEx = null;
        HttpResponseMessage? response = null;
        try
        {
            response = await pullClient.PostAsync("/api/pull", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { startEx = ex; }

        if (startEx != null)
        {
            await out_.WriteAsync(new(OllamaSetupStage.Error,
                $"Failed to start model download: {startEx.Message}"), ct);
            return false;
        }

        using (response)
        {
            Exception? streamEx = null;
            bool success = false;
            try
            {
                await using var stream = await response!.Content.ReadAsStreamAsync(ct);
                using var reader = new System.IO.StreamReader(stream);

                long lastTotal    = -1;
                long lastReceived = -1;

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var evt = JsonSerializer.Deserialize<PullEvent>(line, JsonOpts);
                    if (evt is null) continue;

                    if (evt.Error != null)
                    {
                        await out_.WriteAsync(new(OllamaSetupStage.Error,
                            $"Model download failed: {evt.Error}"), ct);
                        return false;
                    }

                    long total    = evt.Total    ?? lastTotal;
                    long received = evt.Completed ?? lastReceived;
                    lastTotal    = total;
                    lastReceived = received;

                    string sizeHint = total > 0
                        ? $"{FormatBytes(received)} / {FormatBytes(total)}"
                        : evt.Status ?? "";

                    await out_.WriteAsync(new(
                        OllamaSetupStage.PullingModel,
                        $"Downloading AI vision model… {sizeHint}",
                        received, total), ct);

                    if (evt.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        success = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { streamEx = ex; }

            if (streamEx != null)
            {
                await out_.WriteAsync(new(OllamaSetupStage.Error,
                    $"Model download interrupted: {streamEx.Message}"), ct);
                return false;
            }

            if (success)
                AppLog.Info("[OllamaSetup] Model pull complete.");

            return success;
        }
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
            AppLog.Error($"IsModelPresentAsync (model='{modelName}'): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryStartOllamaAsync(string exePath, CancellationToken ct)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath, "serve")
            {
                CreateNoWindow  = true,
                UseShellExecute = false,
            });
        }
        catch { return false; }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await IsOllamaReachableAsync(ct)) return true;
        }
        return false;
    }

    private static string? FindOllamaOnPath()
    {
        try
        {
            using var result = Process.Start(new ProcessStartInfo("where", "ollama")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            var path = result?.StandardOutput.ReadLine()?.Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)       return "";
        if (bytes < 1024)    return $"{bytes} B";
        if (bytes < 1 << 20) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1 << 30) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public void Dispose() => _http.Dispose();

    private record TagsResponse(List<ModelEntry>? Models);
    private record ModelEntry(string Name);
    private record PullEvent(string? Status, long? Total, long? Completed, string? Error);
}
