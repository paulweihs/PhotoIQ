using System.Net.Http;
using System.Runtime.CompilerServices;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Services.Models;

/// <summary>
/// Downloads CLIP model files that are absent from <see cref="AppSettings.ModelsPath"/>.
///
/// Files are fetched from <see cref="AppSettings.ModelDownloadBaseUrl"/> one at a time.
/// Each file is written to a .tmp path first; on success it is atomically renamed to the
/// final destination, so a partial download never leaves a corrupt file in place.
///
/// Progress is reported as overall fraction across all missing files. The service
/// never throws — download errors are surfaced as an Error-stage event so the
/// caller can degrade gracefully (the app continues without CLIP tagging).
/// </summary>
public sealed class ClipModelDownloadService : IClipModelDownloadService
{
    // Files required for both vision and text CLIP engines to initialise.
    internal static readonly string[] RequiredFiles =
    [
        "clip-vit-base-patch32-vision.onnx",
        "clip-vit-base-patch32-text.onnx",
        "vocab.json",
        "merges.txt",
    ];

    // Buffer size for streaming downloads — 64 KB is a reasonable balance between
    // memory use and round-trips on a fast connection.
    private const int BufferSize = 65_536;

    private readonly HttpClient _http;

    public ClipModelDownloadService()
    {
        _http = new HttpClient
        {
            // 30-minute timeout covers slow connections downloading large ONNX files.
            Timeout = TimeSpan.FromMinutes(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PhotoWell/{AppSettings.Version} (model-download)");
    }

    public bool AreModelsInstalled()
        => RequiredFiles.All(f => File.Exists(Path.Combine(AppSettings.ModelsPath, f)));

    public async IAsyncEnumerable<ClipDownloadProgress> EnsureModelsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new(ClipDownloadStage.Checking, "Checking AI model files…");

        var missing = RequiredFiles
            .Where(f => !File.Exists(Path.Combine(AppSettings.ModelsPath, f)))
            .ToList();

        if (missing.Count == 0)
        {
            yield return new(ClipDownloadStage.Done, "AI models ready.", 1.0);
            yield break;
        }

        AppLog.Vision(
            $"[ModelDownload] {missing.Count} model file(s) missing: {string.Join(", ", missing)}");

        // ── Download each missing file ────────────────────────────────────────
        // yield return cannot appear inside a try-catch, so download errors are
        // returned through a result record rather than thrown.
        for (int i = 0; i < missing.Count; i++)
        {
            var fileName = missing[i];
            var fraction = (double)i / missing.Count;

            yield return new(
                ClipDownloadStage.Downloading,
                $"Downloading {fileName} ({i + 1} of {missing.Count})…",
                fraction);

            // Run the actual download in a helper that captures errors.
            var result = await DownloadFileAsync(fileName, i, missing.Count, ct);

            if (result.Error is not null)
            {
                // Log already happened inside the helper.
                yield return new(
                    ClipDownloadStage.Error,
                    $"Could not download {fileName}.\n\n" +
                    $"{result.Error}\n\n" +
                    "PhotoWell will open without AI tagging. Restart to retry.",
                    -1);
                yield break;
            }

            AppLog.Vision(
                $"[ModelDownload] {fileName} installed " +
                $"({result.BytesWritten / 1024.0 / 1024.0:F1} MB)");
        }

        yield return new(
            ClipDownloadStage.Done,
            "AI models downloaded and ready.", 1.0);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private record DownloadResult(long BytesWritten, string? Error);

    private async Task<DownloadResult> DownloadFileAsync(
        string fileName, int index, int total, CancellationToken ct)
    {
        var destPath = Path.Combine(AppSettings.ModelsPath, fileName);
        var tempPath = destPath + ".tmp";
        var url      = AppSettings.ModelDownloadBaseUrl + fileName;

        try
        {
            using var resp = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var msg  = $"HTTP {(int)resp.StatusCode} from model server — {body.Trim()}";
                AppLog.Error($"[ModelDownload] {fileName}: {msg}");
                return new(0, $"Server returned an error ({(int)resp.StatusCode}). Check your internet connection.");
            }

            long written = 0;
            await using (var src  = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst  = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                                   FileShare.None, BufferSize, useAsync: true))
            {
                var buf = new byte[BufferSize];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    written += read;
                }
            }

            // Atomic rename — never leaves a partial file at the real path.
            File.Move(tempPath, destPath, overwrite: true);
            return new(written, null);
        }
        catch (OperationCanceledException)
        {
            File.Delete(tempPath);
            throw; // propagate so EnsureModelsAsync stops and the caller's CT is respected
        }
        catch (Exception ex)
        {
            File.Delete(tempPath);
            AppLog.Error(
                $"[ModelDownload] Failed to download '{fileName}': " +
                $"{ex.GetType().Name}: {ex.Message}");
            return new(0, ex.Message);
        }
    }
}
