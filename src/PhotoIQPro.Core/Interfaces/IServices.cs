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
