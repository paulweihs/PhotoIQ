using System.Net.Http;
using System.Runtime.CompilerServices;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Models;

/// <summary>
/// Downloads face detection model files that are absent from <see cref="AppSettings.ModelsPath"/>.
/// Files are fetched from <see cref="AppSettings.FaceModelDownloadBaseUrl"/>.
/// </summary>
public sealed class FaceModelDownloadService : IFaceModelDownloadService
{
    // Files required for face detection and embedding
    internal static readonly string[] RequiredFiles =
    [
        "ultraface-rfb-640.onnx",
        "arcface-mobilefacenet.onnx",
    ];

    private const int BufferSize = 65_536;
    private readonly HttpClient _http;

    public FaceModelDownloadService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PhotoIQPro/{AppSettings.Version} (face-model-download)");
    }

    public bool AreModelsInstalled()
        => RequiredFiles.All(f => File.Exists(Path.Combine(AppSettings.ModelsPath, f)));

    public async IAsyncEnumerable<object> EnsureModelsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var missing = RequiredFiles
            .Where(f => !File.Exists(Path.Combine(AppSettings.ModelsPath, f)))
            .ToList();

        if (missing.Count == 0)
        {
            yield return (object)new FaceDownloadProgress(FaceDownloadStage.Complete, "Face detection models ready.");
            yield break;
        }

        yield return (object)new FaceDownloadProgress(FaceDownloadStage.Checking, 
            $"Downloading {missing.Count} face detection model file(s)…");

        foreach (var fileName in missing)
        {
            var result = await DownloadFileAsync(fileName, ct);
            if (!result.Success)
            {
                yield return (object)new FaceDownloadProgress(FaceDownloadStage.Error,
                    $"Failed to download {fileName}: {result.Error}");
                yield break;
            }
        }

        yield return (object)new FaceDownloadProgress(FaceDownloadStage.Complete,
            $"Downloaded {missing.Count} face detection model file(s).");
    }

    private async Task<DownloadResult> DownloadFileAsync(string fileName, CancellationToken ct)
    {
        var destPath = Path.Combine(AppSettings.ModelsPath, fileName);
        var tmpPath = destPath + ".tmp";
        var url = AppSettings.FaceModelDownloadBaseUrl + fileName;

        try
        {
            Directory.CreateDirectory(AppSettings.ModelsPath);
            
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, resp.StatusCode.ToString(), 0);

            using var srcStream = await resp.Content.ReadAsStreamAsync(ct);
            using var dstStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[BufferSize];
            int bytesRead;
            long bytesWritten = 0;

            while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await dstStream.WriteAsync(buffer, 0, bytesRead, ct);
                bytesWritten += bytesRead;
            }

            await dstStream.FlushAsync(ct);
            dstStream.Close();

            // Atomic rename
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmpPath, destPath);

            return new(true, null, bytesWritten);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }
            return new(false, ex.Message, 0);
        }
    }

    private record DownloadResult(bool Success, string? Error, long BytesDownloaded);
}

public record FaceDownloadProgress(FaceDownloadStage Stage, string Message);

public enum FaceDownloadStage { Checking, Downloading, Complete, Error, Cancelled }
