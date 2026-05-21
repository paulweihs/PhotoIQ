using PhotoWell.Core.Models;

namespace PhotoWell.Core.Interfaces;

public interface IImportService
{
    Task<MediaFile?> ImportFileAsync(string filePath, CancellationToken ct = default);
    Task<ImportResult> ImportFolderAsync(string folderPath, bool recursive, IProgress<ImportProgress>? progress = null, Action<MediaFile>? onPhotoImported = null, CancellationToken ct = default);
    bool IsSupportedFile(string filePath);

    /// <summary>Re-runs CLIP + LLaVA on an already-imported photo, replacing its AI tags and description.</summary>
    Task<bool> ReanalyzeFileAsync(Guid mediaFileId, IProgress<ImportProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Re-analyzes every photo in the library (or only unanalyzed ones). When skipUserModified is true, photos with a user-edited description are left untouched.</summary>
    Task<ImportResult> ReanalyzeAllAsync(bool unanalyzedOnly, bool skipUserModified = false, IProgress<ImportProgress>? progress = null, Action<MediaFile>? onPhotoAnalyzed = null, CancellationToken ct = default);

    /// <summary>Re-analyzes only photos whose AI description was generated with an outdated model or prompt version. When skipUserModified is true, photos with a user-edited description are left untouched.</summary>
    Task<ImportResult> ReanalyzeOutdatedAsync(bool skipUserModified = false, IProgress<ImportProgress>? progress = null, Action<MediaFile>? onPhotoAnalyzed = null, CancellationToken ct = default, System.Collections.Concurrent.ConcurrentQueue<Guid>? priorityQueue = null);

    /// <summary>Regenerates thumbnails for all photos, replacing whatever was stored on import. Pass resume=true to continue from a previous interrupted run.</summary>
    Task<ImportResult> RegenerateThumbnailsAsync(IProgress<ImportProgress>? progress = null, Action<MediaFile>? onThumbnailRegenerated = null, CancellationToken ct = default, bool resume = false);

    /// <summary>Runs only the Ollama vision pass on a photo that already has CLIP analysis (AnalysisStatus == VisionPending).</summary>
    Task RunVisionForPhotoAsync(Guid mediaFileId, CancellationToken ct = default);

    /// <summary>Reads every EXIF/metadata tag from the file on disk and returns them as flat key/value rows.</summary>
    Task<IReadOnlyList<MetadataTag>> ReadAllTagsAsync(string filePath, CancellationToken ct = default);
}

public record ImportProgress(int Total, int Processed, int Imported, int Skipped, int Failed, string CurrentFile, int Excluded = 0);
public record MetadataTag(string Directory, string Tag, string Value);
public record ImportResult(int TotalFiles, int Imported, int Skipped, int Failed, TimeSpan Duration, List<string> Errors)
{
    /// <summary>True when the import was halted because the Express library limit was reached.</summary>
    public bool LimitReached { get; init; }
}

public interface IExportService
{
    /// <summary>Estimated ZIP size in bytes for a DB-only backup.</summary>
    Task<long> EstimateBackupSizeAsync(bool includeThumbnails);

    /// <summary>Creates a ZIP backup of the database (and optionally thumbnails) at destPath.</summary>
    Task CreateBackupAsync(string destPath, bool includeThumbnails,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Restores the database from a backup ZIP. Closes the DB connection pool before overwriting.</summary>
    Task RestoreBackupAsync(string zipPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Creates a .pwbundle (ZIP) containing the source photo files + metadata.json for the given photo IDs.</summary>
    Task CreateBundleAsync(IReadOnlyList<Guid> photoIds, string destPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Reads the manifest from a .pwbundle without importing. Returns null if the file is invalid.</summary>
    Task<BundleManifest?> ReadBundleManifestAsync(string bundlePath);

    /// <summary>Imports photos from a .pwbundle into the library, appending to existing data.</summary>
    Task<BundleImportResult> ImportBundleAsync(string bundlePath, string photoDestFolder,
        Func<BundleConflict, Task<ConflictResolution>> resolveConflict,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default);
}

public record ExportProgress(int Total, int Processed, string CurrentFile);

public record BundleManifest(string Version, DateTime ExportedAt, int PhotoCount, string SourceMachine);

public record BundleConflict(
    string FileName,
    string FileHash,
    // Existing library values
    int ExistingRating, bool ExistingFavorite, string? ExistingUserCaption,
    string? ExistingAiDescription, string? ExistingUserDescription, IReadOnlyList<string> ExistingTags,
    // Bundle values
    int BundleRating, bool BundleFavorite, string? BundleUserCaption,
    string? BundleAiDescription, string? BundleUserDescription, IReadOnlyList<string> BundleTags);

public enum ConflictResolution { KeepExisting, UseBundle }

public record BundleImportResult(int TotalInBundle, int Imported, int Merged, int Skipped, int Failed,
    TimeSpan Duration, List<string> Errors);

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
    Task<float[]?> GetImageEmbeddingAsync(string imagePath, CancellationToken ct = default);
}

public interface ISemanticSearchService
{
    bool IsAvailable { get; }
    /// <summary>True if the CLIP text model file exists on disk (checked without initialization).</summary>
    bool IsModelAvailable { get; }
    Task<IReadOnlyList<MediaFile>> SearchAsync(string query, int topN = 50);
    /// <summary>Finds photos visually similar to the given photo, using its stored CLIP embedding.</summary>
    Task<IReadOnlyList<MediaFile>> FindSimilarAsync(Guid sourceMediaFileId, int topN = 25);
}

public record TagPrediction(string Label, TagCategory Category, float Confidence);

/// <summary>
/// Ensures an image is in a format suitable for vision model analysis (JPEG or PNG).
/// Returns a PreparedImage whose Path is either the original (no conversion needed)
/// or a temp JPEG copy. Always dispose the result to clean up temp files.
/// Returns null if the file cannot be decoded.
/// </summary>
/// <summary>
/// Writes EXIF metadata tags to photo files on disk using ExifTool.
/// ExifTool is downloaded on first use and cached in %LOCALAPPDATA%\PhotoWell\tools\.
/// </summary>
public interface IExifEditService
{
    /// <summary>True when exiftool.exe is present and ready.</summary>
    bool IsAvailable { get; }

    /// <summary>Downloads exiftool.exe if not already present. Safe to call repeatedly.</summary>
    Task EnsureAvailableAsync(IProgress<string>? status = null, CancellationToken ct = default);

    /// <summary>
    /// Writes a single EXIF tag to <paramref name="filePath"/> in place.
    /// Call EnsureAvailableAsync before this method.
    /// </summary>
    /// <param name="tag">ExifTool tag name, e.g. "DateTimeOriginal".</param>
    /// <param name="value">Raw value string in the format ExifTool expects.</param>
    Task WriteTagAsync(string filePath, string tag, string value, CancellationToken ct = default);
}

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
