namespace PhotoIQPro.Core.Interfaces;

public enum ClipDownloadStage
{
    Checking,
    Downloading,
    Done,
    Error,
}

public record ClipDownloadProgress(
    ClipDownloadStage Stage,
    string Message,
    /// <summary>
    /// 0–1 during Downloading (overall progress across all files); -1 when unknown; 1.0 when Done.
    /// </summary>
    double Fraction = -1);

public interface IClipModelDownloadService
{
    /// <summary>Returns true when every required CLIP model file exists in ModelsPath.</summary>
    bool AreModelsInstalled();

    /// <summary>
    /// Yields progress events while ensuring all required model files are present.
    /// If models are already installed, yields a single Done event and returns.
    /// Downloads any missing files from AppSettings.ModelDownloadBaseUrl, using a
    /// temp file per download so a partial transfer never corrupts the destination.
    /// Never throws — download failures are reported as an Error stage so the caller
    /// can degrade gracefully (the app starts without CLIP tagging).
    /// </summary>
    IAsyncEnumerable<ClipDownloadProgress> EnsureModelsAsync(CancellationToken ct = default);
}
