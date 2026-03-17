using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IImportService
{
    Task<MediaFile?> ImportFileAsync(string filePath, CancellationToken ct = default);
    Task<ImportResult> ImportFolderAsync(string folderPath, bool recursive, IProgress<ImportProgress>? progress = null, CancellationToken ct = default);
    bool IsSupportedFile(string filePath);
}

public record ImportProgress(int Total, int Processed, int Imported, int Skipped, int Failed, string CurrentFile);
public record ImportResult(int TotalFiles, int Imported, int Skipped, int Failed, TimeSpan Duration, List<string> Errors);

public interface IThumbnailService
{
    Task<ThumbnailResult> GenerateThumbnailsAsync(MediaFile mediaFile, CancellationToken ct = default);
    string GetThumbnailPath(Guid mediaFileId, ThumbnailSize size);
}

public enum ThumbnailSize { Small = 150, Medium = 400, Large = 800 }
public record ThumbnailResult(bool Success, string? SmallPath, string? MediumPath, string? LargePath, string? Error);

public interface ITaggingService
{
    bool IsAvailable { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TagPrediction>> GenerateTagsAsync(string imagePath, CancellationToken ct = default);
}

public record TagPrediction(string Label, TagCategory Category, float Confidence);

/// <summary>
/// Ensures an image is in a format suitable for vision model analysis (JPEG or PNG).
/// Returns a PreparedImage whose Path is either the original (no conversion needed)
/// or a temp JPEG copy. Always dispose the result to clean up temp files.
/// Returns null if the file cannot be decoded.
/// </summary>
public interface IImagePreprocessor
{
    Task<PreparedImage?> PrepareAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Wraps a path returned by IImagePreprocessor. Disposing deletes the file if it is
/// a temporary conversion — does nothing if it points to the original.
/// </summary>
public sealed class PreparedImage : IDisposable
{
    public string Path { get; }
    private readonly bool _isTemp;

    public PreparedImage(string path, bool isTemp) { Path = path; _isTemp = isTemp; }

    public void Dispose()
    {
        if (_isTemp)
            try { File.Delete(Path); } catch { /* best-effort */ }
    }
}
