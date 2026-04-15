using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(Guid id);
    Task<int> CountAsync();
}

public interface IMediaFileRepository : IRepository<MediaFile>
{
    Task<MediaFile?> GetByPathAsync(string filePath);
    Task<MediaFile?> GetByHashAsync(string fileHash);
    Task<IEnumerable<MediaFile>> GetFavoritesAsync();
    Task<IEnumerable<MediaFile>> GetUnanalyzedAsync(int limit = 100);
    Task<IEnumerable<MediaFile>> SearchAsync(string query);
    Task<bool> ExistsAsync(string filePath);
    /// <summary>Marks a photo as excluded: hides it from all library queries but keeps the record so reimport is blocked.</summary>
    Task ExcludeAsync(Guid id);
    Task<IEnumerable<DuplicateGroup>> GetDuplicateGroupsAsync();
    Task<Tag> GetOrCreateTagAsync(string name, string normalizedName, TagCategory category, bool isAiGenerated, float confidence);
    Task RemoveTagFromPhotoAsync(Guid photoId, Guid tagId);
    Task<List<string>> GetAllTagNamesAsync();
    Task<IReadOnlyDictionary<string, int>> GetTagPhotoCountsAsync();

    /// <summary>
    /// Rebuilds the FTS5 search index from scratch using current library data.
    /// Called once at startup to ensure existing records are indexed.
    /// </summary>
    Task RebuildFtsIndexAsync();

    /// <summary>
    /// Removes all library entries whose file path is inside <paramref name="folderPath"/>.
    /// When <paramref name="includeSubfolders"/> is true, the entire subtree is removed;
    /// when false, only files whose immediate parent directory equals <paramref name="folderPath"/>
    /// are removed, leaving photos in nested subfolders intact.
    /// Also cleans up the FTS5 search index for all deleted entries.
    /// </summary>
    Task<int> RemoveByFolderAsync(string folderPath, bool includeSubfolders);
    Task BatchUpdateAsync(IReadOnlyList<MediaFile> files);

    /// <summary>
    /// Returns photos whose stored CLIP embedding has cosine similarity ≥ minScore
    /// to the provided query embedding, ordered by descending score.
    /// </summary>
    Task<IEnumerable<MediaFile>> SearchByEmbeddingAsync(float[] queryEmbedding, float minScore = 0.18f, int topN = 50);

    /// <summary>
    /// Returns the full file path of every imported photo. Used by Scan Drives to identify
    /// which found files are already in the library without loading full entities.
    /// </summary>
    Task<HashSet<string>> GetAllFilePathsAsync();

    /// <summary>
    /// Returns all non-excluded photos with their Tags eagerly loaded.
    /// Used by re-analyze operations that need to strip AI tags without a per-photo GetByIdAsync.
    /// </summary>
    Task<IEnumerable<MediaFile>> GetAllWithTagsAsync();

    /// <summary>
    /// Returns all non-excluded photos with AnalysisStatus == VisionPending.
    /// Used at startup to resume background vision processing from a previous session.
    /// </summary>
    Task<IEnumerable<MediaFile>> GetVisionPendingAsync();

    /// <summary>
    /// Returns only the IDs of VisionPending photos. Avoids loading full entities
    /// when only IDs are needed to enqueue background vision jobs.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetVisionPendingIdsAsync();

    /// <summary>
    /// Returns analyzed photos whose AiDescription was generated with a different model or
    /// prompt version than the current ones. Excludes excluded photos and those with no description.
    /// Tags are eagerly loaded so callers can strip AI tags without an extra round-trip.
    /// </summary>
    Task<IEnumerable<MediaFile>> GetOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion);

    /// <summary>Count of photos with outdated AI descriptions.</summary>
    Task<int> CountOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion);

    /// <summary>
    /// Returns the distinct parent directories of all non-excluded photos in the library.
    /// Used by FolderWatcherService to know which folders to monitor for new arrivals.
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctFolderPathsAsync();

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> photos whose DateTaken falls within
    /// ±<paramref name="windowMinutes"/> minutes of <paramref name="referenceDate"/>,
    /// excluding the photo with <paramref name="excludeId"/>. Ordered by DateTaken ascending.
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetNearbyDateAsync(
        DateTime referenceDate, int windowMinutes, Guid excludeId, int maxResults = 20);

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> photos whose GPS coordinates are within
    /// <paramref name="radiusKm"/> kilometres of the given position, excluding
    /// <paramref name="excludeId"/>. Uses a bounding-box pre-filter in SQL then
    /// Haversine distance in memory. Ordered by DateTaken ascending.
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetNearbyLocationAsync(
        double lat, double lon, double radiusKm, Guid excludeId, int maxResults = 20);

    /// <summary>
    /// Returns all non-excluded photos whose IDs are in <paramref name="ids"/>,
    /// ordered by DateTaken ascending then DateImported ascending.
    /// Used by the related-images mode to reload the filtered gallery set.
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetByIdsAsync(IEnumerable<Guid> ids);

    // ── Face detection ────────────────────────────────────────────────────────

    /// <summary>Persists a newly detected Face row (no PersonId yet).</summary>
    Task AddFaceAsync(Face face);

    /// <summary>Returns all faces for a photo, with Person eagerly loaded.</summary>
    Task<IReadOnlyList<Face>> GetFacesForPhotoAsync(Guid mediaFileId);

    /// <summary>Returns photos whose FaceDetectionStatus == Pending, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> GetFaceDetectionPendingIdsAsync(int limit = 200);

    /// <summary>Sets FacesReviewed = true so the "Who is in this photo?" prompt is not shown again.</summary>
    Task MarkFacesReviewedAsync(Guid mediaFileId);

    /// <summary>Returns the distinct MediaFile IDs of all photos that have at least one face linked to <paramref name="personId"/>.</summary>
    Task<IReadOnlyList<Guid>> GetFacePhotoIdsForPersonAsync(Guid personId);

    /// <summary>
    /// Returns non-excluded photos taken on the same month+day as <paramref name="date"/>
    /// in any prior year, ordered by year descending then DateTaken ascending.
    /// </summary>
    Task<IReadOnlyList<MediaFile>> GetOnThisDayAsync(DateTime date);
}

public enum DuplicateGroupType { ExactHash, RawJpegPair }
public record DuplicateGroup(DuplicateGroupType Type, IReadOnlyList<MediaFile> Files);

public interface ILibraryRepository
{
    Task<IReadOnlyList<Library>> GetAllAsync();
    Task<Library> CreateLibraryAsync(string name, string? description = null);
    Task<Album> CreateAlbumAsync(Guid libraryId, string name, string? description = null);
    Task RenameLibraryAsync(Guid id, string newName);
    Task RenameAlbumAsync(Guid id, string newName);
    Task DeleteLibraryAsync(Guid id);
    Task DeleteAlbumAsync(Guid id);
    Task AddPhotosToAlbumAsync(Guid albumId, IReadOnlyList<Guid> photoIds);
    Task RemovePhotosFromAlbumAsync(Guid albumId, IReadOnlyList<Guid> photoIds);
    Task<IEnumerable<MediaFile>> GetPhotosByAlbumAsync(Guid albumId);
    Task<Dictionary<Guid, int>> GetAlbumPhotoCountsAsync();
}
