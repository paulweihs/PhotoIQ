namespace PhotoWell.Core.Interfaces;

/// <summary>Service for downloading face detection model files on demand.</summary>
public interface IFaceModelDownloadService
{
    /// <summary>Returns true if all required face models are present and ready to use.</summary>
    bool AreModelsInstalled();

    /// <summary>
    /// Asynchronously ensures all required face detection models are downloaded.
    /// Yields progress updates for each download stage. Never throws; errors are reported as events.
    /// </summary>
    IAsyncEnumerable<object> EnsureModelsAsync(CancellationToken ct = default);
}
