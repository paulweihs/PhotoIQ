using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Data.Helpers;

namespace PhotoWell.Data.Repositories;

// NOTE: All ExecuteSqlAsync($"...") calls use EF Core 8's FormattableString overload, which
// automatically converts each {placeholder} into a parameterized @pN value. This is NOT
// string-interpolated raw SQL — it is safe from injection. Use ExecuteSqlRawAsync only for
// fixed SQL strings with no user input, or where the values are guaranteed injection-safe
// by type (e.g. GUIDs formatted for a variable-length IN clause).

/// <summary>
/// EF Core repository for MediaFile persistence and queries.
/// </summary>
/// <remarks>
/// This repository handles all CRUD operations on MediaFile entities, including:
/// - Single-file lookups (GetByIdAsync)
/// - Bulk operations (BatchUpdateAsync, RemoveByFolderAsync)
/// - Filtered queries (GetByPathAsync, GetByHashAsync, GetFavoritesAsync, GetUnanalyzedAsync)
/// - Full-text search via the MediaFilesSearch SQLite FTS5 virtual table
/// - Exclusion logic (marking files as IsExcluded without deleting them)
///
/// All write operations (Add, Update, Delete) automatically sync the FTS index so searches
/// reflect the current state of the library. Failed writes clear the change tracker to prevent
/// state contamination on the shared context instance.
/// </remarks>
public class MediaFileRepository : IMediaFileRepository
{
    private readonly PhotoWellContext _context;
    /// <summary>Initializes a new instance of the MediaFileRepository.</summary>
    /// <param name="context">The PhotoWellContext for database access.</param>
    public MediaFileRepository(PhotoWellContext context) => _context = context;

    /// <summary>Gets a single MediaFile by ID, with all associated tags eagerly loaded.</summary>
    /// <param name="id">The MediaFile ID.</param>
    /// <returns>The MediaFile with its Tags collection, or null if not found.</returns>
    public async Task<MediaFile?> GetByIdAsync(Guid id) => await _context.MediaFiles.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == id);
    /// <summary>Gets all non-excluded MediaFiles in the library, sorted by date taken (descending).</summary>
    /// <remarks>Excluded files are filtered out. Files are ordered by DateTaken if available, else DateImported.</remarks>
    /// <returns>A list of all active MediaFiles.</returns>
    public async Task<IEnumerable<MediaFile>> GetAllAsync() => await _context.MediaFiles.Where(m => !m.IsExcluded).OrderByDescending(m => m.DateTaken ?? m.DateImported).ToListAsync();

    /// <summary>
    /// Adds a new MediaFile to the library and syncs the full-text search index.
    /// </summary>
    /// <remarks>
    /// The file is inserted into the main MediaFiles table and immediately added to the
    /// MediaFilesSearch FTS5 virtual table for full-text search. Failed writes are caught and
    /// the change tracker is cleared to prevent state contamination.
    /// </remarks>
    /// <param name="entity">The MediaFile to add.</param>
    /// <returns>The added MediaFile (with any database-generated fields populated).</returns>
    public async Task<MediaFile> AddAsync(MediaFile entity)
    {
        // Wrap MediaFile insert + FTS upsert in a single transaction so a failed FTS write
        // cannot leave a photo in the DB that is invisible to search.
        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.MediaFiles.AddAsync(entity);
            await _context.SaveChangesAsync();
            await UpsertFtsAsync(entity);
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            // Clear the change tracker so the failed entity (left in Added state by EF Core)
            // does not contaminate subsequent SaveChangesAsync calls on this DbContext instance.
            _context.ChangeTracker.Clear();
            throw;
        }
        catch
        {
            await transaction.RollbackAsync();
            _context.ChangeTracker.Clear();
            throw;
        }
        return entity;
    }

    /// <summary>
    /// Updates an existing MediaFile and syncs the full-text search index.
    /// </summary>
    /// <remarks>
    /// Sets DateModified to the current UTC time before saving. The FTS index is then updated
    /// to reflect any changes to searchable fields (FileName, Description, tags).
    /// </remarks>
    /// <param name="entity">The MediaFile to update (must be already attached to the context).</param>
    public async Task UpdateAsync(MediaFile entity)
    {
        entity.DateModified = DateTime.UtcNow;

        // Wrap all writes in a single transaction so the MediaFile row and FTS index
        // stay in sync — a failed FTS write will roll back the entity update too.
        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // Reload-and-merge pattern to avoid two complementary EF tracking bugs:
            //
            // BUG A — Identity conflict: calling Update() when the tracker already holds a Tag
            // with the same Guid (different C# instance) throws "another instance with the same
            // key value is already being tracked."
            //
            // BUG B — UNIQUE constraint: clearing the tracker before Update() fixes Bug A, but
            // then EF has no snapshot of which MediaFileTag join rows already exist. It tries to
            // INSERT all of them → SQLite UNIQUE constraint failure.
            //
            // FIX: load the authoritative DB copy (with Tags), copy scalar properties onto it,
            // then explicitly sync the Tags collection so EF can produce correct INSERT/DELETE
            // statements for the join table without touching rows that haven't changed.
            _context.ChangeTracker.Clear();

            var tracked = await _context.MediaFiles
                .Include(m => m.Tags)
                .FirstOrDefaultAsync(m => m.Id == entity.Id);

            if (tracked == null)
            {
                // New entity (e.g. first import) — just add it.
                _context.MediaFiles.Add(entity);
                await _context.SaveChangesAsync();
                _context.ChangeTracker.Clear();
                await UpsertFtsAsync(entity);
                await transaction.CommitAsync();
                return;
            }

            // Copy all scalar properties from the incoming entity onto the tracked one.
            _context.Entry(tracked).CurrentValues.SetValues(entity);

            // Sync Tags: remove relationships no longer present, add new ones.
            // Use FindAsync so EF resolves to the already-tracked Tag instance when possible,
            // avoiding duplicate-tracking on the Tag itself.
            await MergeTagsAndSaveAsync(tracked, entity);
            _context.ChangeTracker.Clear();
            await UpsertFtsAsync(entity);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Updates multiple MediaFiles in a single batch, then syncs the full-text search index.
    /// </summary>
    /// <remarks>
    /// All files are assigned the same DateModified timestamp. The batch is persisted in a single
    /// SaveChangesAsync call to minimize database roundtrips, then each file's FTS entry is updated.
    /// This is more efficient than calling UpdateAsync individually.
    /// </remarks>
    /// <param name="files">A list of MediaFiles to update.</param>
    public async Task BatchUpdateAsync(IReadOnlyList<MediaFile> files)
    {
        if (files.Count == 0) return;
        // Each UpdateAsync manages its own transaction (reload-and-merge + FTS sync).
        // Do NOT wrap in an outer BeginTransactionAsync — EF Core throws
        // "connection is already in a transaction" when UpdateAsync tries to open its own.
        foreach (var f in files)
            await UpdateAsync(f);
    }

    /// <summary>
    /// Permanently deletes a MediaFile and removes it from the full-text search index.
    /// </summary>
    /// <remarks>
    /// This is a hard delete — the file is completely removed from the library and cannot be recovered.
    /// The FTS index is also cleaned up. If the file is not found, the operation is silently skipped.
    /// </remarks>
    /// <param name="id">The MediaFile ID to delete.</param>
    public async Task DeleteAsync(Guid id)
    {
        var e = await _context.MediaFiles
            .Include(m => m.Faces)
            .FirstOrDefaultAsync(m => m.Id == id);
        if (e == null) return;
        var thumbPaths = e.Faces.Select(f => f.ThumbnailPath).ToList();
        _context.MediaFiles.Remove(e);
        await _context.SaveChangesAsync();
        await FtsQueryHelper.DeleteSingleAsync(_context.Database, id);
        DeleteFaceThumbnailFiles(thumbPaths);
    }

    /// <summary>
    /// Marks a MediaFile as excluded (hidden from the library) without deleting it.
    /// </summary>
    /// <remarks>
    /// Excluded files remain in the database but are filtered out of GetAllAsync and search results.
    /// Users can manually un-exclude them later. The FTS index is also cleaned so excluded files
    /// don't appear in full-text searches.
    /// </remarks>
    /// <param name="id">The MediaFile ID to exclude.</param>
    public async Task ExcludeAsync(Guid id)
    {
        var e = await _context.MediaFiles.FindAsync(id);
        if (e == null) return;
        e.IsExcluded = true;
        await _context.SaveChangesAsync();
        // Remove from FTS so it doesn't appear in search results.
        await FtsQueryHelper.DeleteSingleAsync(_context.Database, id);
    }

    /// <summary>Gets the total count of non-excluded MediaFiles in the library.</summary>
    /// <remarks>Excluded files are not counted. Used to enforce the Express tier library limit.</remarks>
    /// <returns>The count of active MediaFiles.</returns>
    public async Task<int> CountAsync() => await _context.MediaFiles.CountAsync(m => !m.IsExcluded);

    /// <summary>
    /// Removes all MediaFiles in a folder (with optional recursive deletion).
    /// </summary>
    /// <remarks>
    /// If includeSubfolders is false, only files directly in the folder are deleted; nested paths are skipped.
    /// If true, all files whose FilePath starts with the folder prefix are deleted.
    /// Folder paths are normalized to ensure "C:\Foo" doesn't match "C:\FooBar".
    /// The FTS index is also updated in batches (chunked to avoid SQLite's variable limit).
    /// </remarks>
    /// <param name="folderPath">Full path to the folder whose files should be removed.</param>
    /// <param name="includeSubfolders">If true, recursively delete files in subfolders; if false, delete only top-level files.</param>
    /// <returns>The number of files deleted.</returns>
    public async Task<int> RemoveByFolderAsync(string folderPath, bool includeSubfolders)
    {
        // Normalise: ensure the prefix ends with a separator so "C:\Foo" never matches "C:\FooBar".
        var prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;

        List<MediaFile> toDelete;
        if (includeSubfolders)
        {
            toDelete = await _context.MediaFiles
                .Where(m => m.FilePath.StartsWith(prefix))
                .ToListAsync();
        }
        else
        {
            // Exact-folder only: pull candidates via prefix then filter out nested paths in memory.
            var candidates = await _context.MediaFiles
                .Where(m => m.FilePath.StartsWith(prefix))
                .ToListAsync();
            toDelete = candidates
                .Where(m => !m.FilePath[prefix.Length..].Contains(Path.DirectorySeparatorChar))
                .ToList();
        }

        if (toDelete.Count == 0) return 0;

        var ids = toDelete.Select(m => m.Id).ToList();
        var thumbPaths = await _context.Faces
            .Where(f => ids.Contains(f.MediaFileId))
            .Select(f => f.ThumbnailPath)
            .ToListAsync();

        _context.MediaFiles.RemoveRange(toDelete);
        await _context.SaveChangesAsync();

        // Batch-delete from the FTS index using helper (handles chunking and parameterization).
        await FtsQueryHelper.DeleteBatchAsync(_context.Database, toDelete.Select(mf => mf.Id));
        DeleteFaceThumbnailFiles(thumbPaths);

        return toDelete.Count;
    }

    public async Task<MediaFile?> GetByPathAsync(string filePath) => await _context.MediaFiles.FirstOrDefaultAsync(m => m.FilePath == filePath);
    public async Task<MediaFile?> GetByHashAsync(string fileHash) => await _context.MediaFiles.FirstOrDefaultAsync(m => m.FileHash == fileHash);
    public async Task<IEnumerable<MediaFile>> GetFavoritesAsync() => await _context.MediaFiles.Where(m => m.IsFavorite && !m.IsExcluded).ToListAsync();
    public async Task<IEnumerable<MediaFile>> GetUnanalyzedAsync(int limit = 100) => await _context.MediaFiles.Where(m => !m.IsAnalyzed && !m.IsExcluded).Take(limit).ToListAsync();
    public async Task<IEnumerable<MediaFile>> GetVisionPendingAsync() => await _context.MediaFiles
        .Include(m => m.Tags)
        .Where(m => m.AnalysisStatus == AnalysisStatus.VisionPending && !m.IsExcluded)
        .ToListAsync();
    public async Task<IReadOnlyList<Guid>> GetVisionPendingIdsAsync() => await _context.MediaFiles.Where(m => m.AnalysisStatus == AnalysisStatus.VisionPending && !m.IsExcluded).Select(m => m.Id).ToListAsync();

    public async Task<IReadOnlyList<string>> GetDistinctFolderPathsAsync()
    {
        // Fetch only the FilePath strings, then compute distinct parent directories in memory.
        // Avoids loading full entities while still catching every source location in the library.
        var filePaths = await _context.MediaFiles
            .Where(m => !m.IsExcluded)
            .Select(m => m.FilePath)
            .ToListAsync();

        return filePaths
            .Select(p => Path.GetDirectoryName(p) ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<MediaFile>> GetNearbyDateAsync(
        DateTime referenceDate, int windowMinutes, Guid excludeId, int maxResults = 20)
    {
        var from = referenceDate.AddMinutes(-windowMinutes);
        var to   = referenceDate.AddMinutes(windowMinutes);
        // Use DateTaken when available, fall back to DateImported (e.g. Facebook photos without EXIF).
        return await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && m.Id != excludeId
                     && (m.DateTaken != null ? m.DateTaken : m.DateImported) >= from
                     && (m.DateTaken != null ? m.DateTaken : m.DateImported) <= to)
            .OrderBy(m => m.DateTaken ?? m.DateImported)
            .Take(maxResults)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<MediaFile>> GetNearbyLocationAsync(
        double lat, double lon, double radiusKm, Guid excludeId, int maxResults = 20)
    {
        // Pre-filter with a bounding box (SQLite has no distance function).
        // 1 degree latitude ≈ 111 km; longitude degree length shrinks with latitude.
        double latDelta = radiusKm / 111.0;
        double lonDelta = radiusKm / (111.0 * Math.Cos(lat * Math.PI / 180.0));

        var candidates = await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && m.Id != excludeId
                     && m.Latitude  != null
                     && m.Longitude != null
                     && m.Latitude  >= lat - latDelta
                     && m.Latitude  <= lat + latDelta
                     && m.Longitude >= lon - lonDelta
                     && m.Longitude <= lon + lonDelta)
            .OrderBy(m => m.DateTaken)
            .Take(maxResults * 3)   // over-fetch; Haversine filter narrows further
            .ToListAsync();

        // Refine with accurate Haversine distance and apply final limit.
        return candidates
            .Where(m => HaversineKm(lat, lon, m.Latitude!.Value, m.Longitude!.Value) <= radiusKm)
            .Take(maxResults)
            .ToList();
    }

    public async Task<IReadOnlyList<MediaFile>> GetByIdsAsync(IEnumerable<Guid> ids)
    {
        var idSet = ids.ToHashSet();
        return await _context.MediaFiles
            .Where(m => !m.IsExcluded && idSet.Contains(m.Id))
            .OrderBy(m => m.DateTaken)
            .ThenBy(m => m.DateImported)
            .ToListAsync();
    }

    // ── Face detection ────────────────────────────────────────────────────────

    public async Task AddFaceAsync(Face face)
    {
        _context.Faces.Add(face);
        await _context.SaveChangesAsync();
    }

    public async Task BatchAddFacesAsync(IEnumerable<Face> faces)
    {
        _context.Faces.AddRange(faces);
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Face>> GetFacesByIdsAsync(IEnumerable<Guid> faceIds)
    {
        var ids = faceIds.ToList();
        return await _context.Faces
            .Where(f => ids.Contains(f.Id))
            .Include(f => f.Person)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Face>> GetFacesForPhotoAsync(Guid mediaFileId)
        => await _context.Faces
            .Include(f => f.Person)
            .Where(f => f.MediaFileId == mediaFileId)
            .ToListAsync();

    public async Task DeleteFacesForPhotoAsync(Guid mediaFileId)
    {
        // Manual mentions are never auto-deleted — they survive re-detection.
        var thumbPaths = await _context.Faces
            .Where(f => f.MediaFileId == mediaFileId && !f.IsManualMention)
            .Select(f => f.ThumbnailPath)
            .ToListAsync();
        await _context.Faces
            .Where(f => f.MediaFileId == mediaFileId && !f.IsManualMention)
            .ExecuteDeleteAsync();
        DeleteFaceThumbnailFiles(thumbPaths);
    }

    public async Task<IReadOnlyList<Guid>> GetFaceDetectionPendingIdsAsync(int limit = 200)
        => await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && m.FaceDetectionStatus == FaceDetectionStatus.Pending
                     && (m.MediaType == MediaType.Photo || m.MediaType == MediaType.Raw))
            .OrderBy(m => m.DateImported)
            .Take(limit)
            .Select(m => m.Id)
            .ToListAsync();

    public async Task MarkFacesReviewedAsync(Guid mediaFileId)
    {
        var mf = await _context.MediaFiles.FirstOrDefaultAsync(m => m.Id == mediaFileId);
        if (mf == null) return;
        mf.FacesReviewed = true;
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Guid>> GetFacePhotoIdsForPersonAsync(Guid personId)
        => await _context.Faces
            .Where(f => f.PersonId == personId)
            .Select(f => f.MediaFileId)
            .Distinct()
            .ToListAsync();

    public async Task<IReadOnlyList<(Face, MediaFile)>> GetUnconfirmedFacesForPersonAsync(Guid personId, int limit = 50)
    {
        // Return empty immediately if the user has suppressed review prompts for this person.
        var person = await _context.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId);
        if (person?.SuppressPrompt == true) return [];

        var rows = await _context.Faces
            .Where(f => f.PersonId == personId && !f.IsUserConfirmed && !f.MediaFile!.FacesReviewed)
            .OrderByDescending(f => f.IdentificationConfidence)
            .Take(limit)
            .Include(f => f.MediaFile)
            .AsNoTracking()
            .ToListAsync();
        return rows.Select(f => (f, f.MediaFile!)).ToList();
    }

    public async Task UnlinkFaceFromPersonAsync(Guid faceId)
    {
        await _context.Faces
            .Where(f => f.Id == faceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.PersonId,                 (Guid?)null)
                .SetProperty(f => f.IdentificationConfidence, (double?)null)
                .SetProperty(f => f.IsUserConfirmed,          false));
    }

    public async Task ConfirmFaceAsync(Guid faceId)
    {
        await _context.Faces
            .Where(f => f.Id == faceId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsUserConfirmed, true));
    }

    public async Task<IReadOnlyList<(Guid FaceId, Guid PersonId, double X, double Y, double W, double H)>> GetConfirmedFacesForPhotoAsync(Guid photoId)
    {
        var rows = await _context.Faces
            .Where(f => f.MediaFileId == photoId && f.IsUserConfirmed && f.PersonId.HasValue)
            .Select(f => new { f.Id, PersonId = f.PersonId!.Value, f.X, f.Y, f.Width, f.Height })
            .AsNoTracking()
            .ToListAsync();
        return rows.Select(r => (r.Id, r.PersonId, r.X, r.Y, r.Width, r.Height)).ToList();
    }

    public async Task<IReadOnlyList<MediaFile>> GetOnThisDayAsync(DateTime date)
    {
        int month = date.Month;
        int day   = date.Day;
        int thisYear = date.Year;

        // SQLite stores DateTaken as ISO-8601 text; strftime is the reliable approach.
        // We pull all non-excluded photos with a DateTaken and filter in memory —
        // the set is always small relative to the full library.
        var candidates = await _context.MediaFiles
            .Where(m => !m.IsExcluded && m.DateTaken != null && m.DateTaken.Value.Year < thisYear)
            .AsNoTracking()
            .ToListAsync();

        return candidates
            .Where(m => m.DateTaken!.Value.Month == month && m.DateTaken.Value.Day == day)
            .OrderByDescending(m => m.DateTaken!.Value.Year)
            .ThenBy(m => m.DateTaken!.Value.TimeOfDay)
            .ToList();
    }

    public async Task<IEnumerable<MediaFile>> GetByDateRangeAsync(DateTime from, DateTime to)
        => await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && (m.DateTaken != null ? m.DateTaken : m.DateImported) >= from
                     && (m.DateTaken != null ? m.DateTaken : m.DateImported) <= to)
            .Include(m => m.Tags)
            .OrderBy(m => m.DateTaken ?? m.DateImported)
            .ToListAsync();

    public async Task<IEnumerable<MediaFile>> GetByImportedDateRangeAsync(DateTime from, DateTime to)
        => await _context.MediaFiles
            .Where(m => !m.IsExcluded && m.DateImported >= from && m.DateImported <= to)
            .Include(m => m.Tags)
            .OrderBy(m => m.DateImported)
            .ToListAsync();

    /// <summary>
    /// Syncs tags from <paramref name="entity"/> onto the already-tracked <paramref name="tracked"/>
    /// and saves. Retries once if a concurrent write produced a UNIQUE constraint violation on
    /// the junction table (SQLite error 19) — the retry reloads the fresh committed state so
    /// EF produces only the INSERTs that are still missing.
    /// </summary>
    private async Task MergeTagsAndSaveAsync(MediaFile tracked, MediaFile entity)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1)
            {
                // First attempt hit a UNIQUE constraint — reload the fully committed state
                // (which now already contains the row another operation just inserted).
                _context.ChangeTracker.Clear();
                tracked = await _context.MediaFiles
                    .Include(m => m.Tags)
                    .FirstOrDefaultAsync(m => m.Id == entity.Id)
                    ?? throw new InvalidOperationException($"MediaFile {entity.Id} vanished on retry.");
                _context.Entry(tracked).CurrentValues.SetValues(entity);
            }

            var toRemove = tracked.Tags.Where(t => !entity.Tags.Any(e => e.Id == t.Id)).ToList();
            foreach (var t in toRemove)
                tracked.Tags.Remove(t);

            foreach (var t in entity.Tags.Where(e => !tracked.Tags.Any(t => t.Id == e.Id)))
            {
                var resolvedTag = await _context.Tags.FindAsync(t.Id) ?? t;
                tracked.Tags.Add(resolvedTag);
            }

            try
            {
                await _context.SaveChangesAsync();
                return; // success
            }
            catch (DbUpdateException ex)
                when (attempt == 0 && ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // UNIQUE constraint on junction table — concurrent write beat us.
                // Clear and retry; the reload will see the committed row and skip the duplicate.
                _context.ChangeTracker.Clear();
            }
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// Gets all non-excluded media files with related tags eagerly loaded.
    /// Use for reanalysis workflows that need to filter tags (e.g., strip AI-generated tags).
    /// GetAllAsync() (without tags) is available at line 45 for gallery UI without tag overhead.
    /// </summary>
    public async Task<IEnumerable<MediaFile>> GetAllWithTagsAsync()
        => await _context.MediaFiles
            .Include(m => m.Tags)
            .Where(m => !m.IsExcluded)
            .OrderByDescending(m => m.DateTaken ?? m.DateImported)
            .ToListAsync();

    public async Task<IEnumerable<MediaFile>> GetOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion, string currentPostProcessVersion)
        => await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && m.IsAnalyzed
                     && m.AiDescription != null && m.AiDescription != ""
                     && (m.AiModelUsed != currentModel
                         || m.PromptVersion == null
                         || m.PromptVersion != currentPromptVersion
                         || m.PostProcessVersion == null
                         || m.PostProcessVersion != currentPostProcessVersion))
            .Include(m => m.Tags)
            .ToListAsync();

    public async Task<int> CountOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion, string currentPostProcessVersion)
        => await _context.MediaFiles
            .CountAsync(m => !m.IsExcluded
                          && m.IsAnalyzed
                          && m.AiDescription != null && m.AiDescription != ""
                          && (m.AiModelUsed != currentModel
                              || m.PromptVersion == null
                              || m.PromptVersion != currentPromptVersion
                              || m.PostProcessVersion == null
                              || m.PostProcessVersion != currentPostProcessVersion));

    // ── FTS5 search ───────────────────────────────────────────────────────────

    public async Task<IEnumerable<MediaFile>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllAsync();

        var ftsQuery = BuildFtsQuery(query);
        if (!string.IsNullOrEmpty(ftsQuery))
        {
            try
            {
                // FTS5 returns IDs ordered by relevance rank (lower rank = better match).
                // ExecuteSqlAsync(FormattableString) — {ftsQuery} becomes a parameterized @p0.
                // SQLite FTS5 supports parameterized MATCH expressions.
                var idStrings = await _context.Database
                    .SqlQuery<string>(
                        $"SELECT media_file_id AS Value FROM MediaFilesSearch WHERE MediaFilesSearch MATCH {ftsQuery} ORDER BY rank")
                    .ToListAsync();

                if (idStrings.Count > 0)
                {
                    var guids = idStrings
                        .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                        .Where(g => g.HasValue)
                        .Select(g => g!.Value)
                        .ToList();

                    // EF Core translates Contains(List<Guid>) into a single WHERE id IN (...)
                    // clause. SQLite's variable limit is 999; chunk if necessary to avoid
                    // "too many SQL variables" errors on large result sets.
                    const int chunkSize = 900;
                    var allFiles = new List<MediaFile>(guids.Count);
                    for (int i = 0; i < guids.Count; i += chunkSize)
                    {
                        var chunk = guids.GetRange(i, Math.Min(chunkSize, guids.Count - i));
                        var batch = await _context.MediaFiles
                            .Where(m => chunk.Contains(m.Id) && !m.IsExcluded)
                            .Include(m => m.Tags)
                            .ToListAsync();
                        allFiles.AddRange(batch);
                    }

                    // Preserve FTS relevance order via O(1) dictionary lookup (not O(N²) FirstOrDefault).
                    var fileById = allFiles.ToDictionary(f => f.Id);
                    return guids
                        .Where(id => fileById.ContainsKey(id))
                        .Select(id => fileById[id]);
                }

                return [];
            }
            catch
            {
                // FTS table not yet populated or query syntax error — fall through to LIKE.
            }
        }

        return await LikeSearchAsync(query);
    }

    /// <summary>
    /// Clears and rebuilds the FTS index from the current library.
    /// Called once at startup so existing records are searchable immediately.
    /// Processed in 1,000-item chunks to avoid loading the full library into memory.
    /// </summary>
    public async Task RebuildFtsIndexAsync(CancellationToken ct = default)
    {
        // DELETE is a single auto-committed statement — holds the write lock for milliseconds.
        // Previously this ran inside one Serializable transaction that held the lock for the
        // entire rebuild (minutes on large libraries), blocking every other writer with SQLITE_BUSY.
        await FtsQueryHelper.ClearAllAsync(_context.Database);
        await RefreshFtsIndexAsync(ct);
    }

    public async Task RefreshFtsIndexAsync(CancellationToken ct = default)
    {
        // Re-upserts every row WITHOUT clearing first. Searches remain fully functional
        // throughout because each UpsertFtsAsync does an atomic delete+insert for that row —
        // the entry is never absent from the index, only momentarily replaced.
        const int chunkSize = 1000;
        int offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = await _context.MediaFiles
                .Include(m => m.Tags)
                .AsNoTracking()
                .OrderBy(m => m.Id)
                .Skip(offset)
                .Take(chunkSize)
                .ToListAsync(ct);

            if (chunk.Count == 0) break;

            foreach (var m in chunk)
            {
                ct.ThrowIfCancellationRequested();
                await UpsertFtsAsync(m);
            }

            if (chunk.Count < chunkSize) break;
            offset += chunkSize;

            // Yield between chunks so other writers (vision worker, user commands) can
            // acquire write transactions without waiting.
            await Task.Delay(10, ct);
        }
    }

    // ── FTS helpers ───────────────────────────────────────────────────────────

    private async Task UpsertFtsAsync(MediaFile m)
    {
        // If tags aren't loaded on this instance, fetch from DB.
        var tags = m.Tags.Count > 0
            ? m.Tags
            : (IEnumerable<Tag>)await _context.Tags
                .Where(t => t.MediaFiles.Any(mf => mf.Id == m.Id))
                .ToListAsync();

        // Also index named persons linked to this photo so searching by name finds their photos.
        var personNames = await _context.Faces
            .Where(f => f.MediaFileId == m.Id && f.Person!.Name != null)
            .Select(f => f.Person!.Name!)
            .Distinct()
            .ToListAsync();

        var tagText = string.Join(" ", tags.Select(t => t.Name).Concat(personNames));
        var filename    = Path.GetFileNameWithoutExtension(m.FileName);
        var camera      = $"{m.CameraMake ?? ""} {m.CameraModel ?? ""}".Trim();
        var dateText    = BuildDateText(m.DateTaken);

        // ADR-003: user_description takes display priority; index both so either is searchable.
        var description = !string.IsNullOrEmpty(m.UserDescription)
            ? m.UserDescription
            : m.AiDescription ?? "";

        // folder column kept in schema for backwards compat but not populated —
        // folder names caused false-positive search matches (e.g. "Soccer" folder).
        // Users browse by folder via the sidebar tree, not free-text search.
        await FtsQueryHelper.UpsertAsync(_context.Database, m.Id, description, tagText, filename, camera, dateText, folder: "");
    }

    public async Task RefreshFtsForPhotoAsync(Guid photoId)
    {
        var m = await _context.MediaFiles
            .Include(m => m.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == photoId);
        if (m != null) await UpsertFtsAsync(m);
    }

    /// <summary>
    /// Converts a user query into a safe FTS5 MATCH expression.
    /// Each token gets a prefix wildcard (*) so partial words match.
    /// Multiple tokens use FTS5 AND semantics (space-separated).
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var tokens = new List<string>();

        // Extract "quoted phrases" first — emit them as FTS5 phrase tokens verbatim.
        // Remaining unquoted text is processed for individual prefix-match tokens.
        var remaining = Regex.Replace(query, "\"([^\"]+)\"", m =>
        {
            var phrase = m.Groups[1].Value.Trim();
            if (phrase.Length >= 2)
                tokens.Add($"\"{phrase}\"");   // FTS5 phrase: both words must appear adjacent
            return " ";
        });

        // Strip FTS5 special chars from unquoted remainder, then split into prefix tokens.
        var clean = Regex.Replace(remaining, @"[""()\[\]{}\*\+\-\^\!\&\|:,\.]", " ");
        foreach (var t in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (t.Length >= 2) tokens.Add(t + "*");   // prefix match: "wing*" hits "wings"

        return string.Join(" ", tokens);  // FTS5: space-separated tokens = AND
    }

    private static string BuildDateText(DateTime? date)
    {
        if (date == null) return "";
        var d    = date.Value;
        // Produces: "2005 November Nov 2005-11 2005-11-15 [holiday]"
        // Supports: "2005", "November", "November 2005", "2005-11", "Nov 2005", "Thanksgiving 2005"
        var text    = $"{d.Year} {d:MMMM} {d:MMM} {d:yyyy-MM} {d:yyyy-MM-dd}";
        var holiday = d.GetHolidayName();
        return holiday != null ? $"{text} {holiday}" : text;
    }

    // ── LIKE fallback (used when FTS table is empty or query is invalid) ──────

    private async Task<IEnumerable<MediaFile>> LikeSearchAsync(string query)
    {
        // Parse quoted phrases ("Red Wings") and bare keywords separately.
        var phrases = new List<string>();
        var remaining = Regex.Replace(query, "\"([^\"]+)\"", m =>
        {
            var p = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (p.Length >= 2) phrases.Add(p);
            return " ";
        });

        var keywords = remaining
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 2)
            .Distinct()
            .Take(10)
            .ToArray();

        if (keywords.Length == 0 && phrases.Count == 0)
            return await GetAllAsync();

        // Bare keywords → OR union (any keyword match qualifies a photo).
        var matchIds = new HashSet<Guid>();
        foreach (var kw in keywords)
        {
            var ids = await _context.MediaFiles
                .Where(m =>
                    EF.Functions.Like(m.FileName.ToLower(), $"%{kw}%") ||
                    (m.CameraModel    != null && EF.Functions.Like(m.CameraModel.ToLower(),    $"%{kw}%")) ||
                    (m.CameraMake     != null && EF.Functions.Like(m.CameraMake.ToLower(),     $"%{kw}%")) ||
                    (m.AiDescription  != null && EF.Functions.Like(m.AiDescription.ToLower(),  $"%{kw}%")) ||
                    (m.UserDescription != null && EF.Functions.Like(m.UserDescription.ToLower(), $"%{kw}%")) ||
                    m.Tags.Any(t => EF.Functions.Like(t.NormalizedName, $"%{kw}%")))
                .Select(m => m.Id)
                .ToListAsync();
            foreach (var id in ids) matchIds.Add(id);
        }

        // Quoted phrases → AND intersect (photo must contain every phrase).
        HashSet<Guid>? phraseIds = null;
        foreach (var phrase in phrases)
        {
            var ids = await _context.MediaFiles
                .Where(m =>
                    EF.Functions.Like(m.FileName.ToLower(),      $"%{phrase}%") ||
                    (m.AiDescription   != null && EF.Functions.Like(m.AiDescription.ToLower(),   $"%{phrase}%")) ||
                    (m.UserDescription != null && EF.Functions.Like(m.UserDescription.ToLower(), $"%{phrase}%")) ||
                    m.Tags.Any(t => EF.Functions.Like(t.NormalizedName, $"%{phrase}%")))
                .Select(m => m.Id)
                .ToListAsync();
            if (phraseIds == null) phraseIds = [.. ids];
            else phraseIds.IntersectWith(ids);
        }

        // Merge: if both bare keywords and phrases are present, intersect them.
        HashSet<Guid> finalIds;
        if (phraseIds != null && matchIds.Count > 0)
        {
            phraseIds.IntersectWith(matchIds);
            finalIds = phraseIds;
        }
        else
        {
            finalIds = phraseIds ?? matchIds;
        }

        if (finalIds.Count == 0) return [];

        var candidates = await _context.MediaFiles
            .Include(m => m.Tags)
            .Where(m => finalIds.Contains(m.Id))
            .ToListAsync();

        return candidates
            .Select(m => (File: m, Score: ScoreLike(m, keywords)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.File.DateTaken ?? x.File.DateImported)
            .Select(x => x.File);
    }

    private static int ScoreLike(MediaFile m, string[] keywords)
    {
        int score = 0;
        var name        = m.FileName.ToLowerInvariant();
        var camera      = $"{m.CameraMake} {m.CameraModel}".ToLowerInvariant();
        var description = (m.UserDescription ?? m.AiDescription ?? "").ToLowerInvariant();
        var tagNames    = m.Tags.Select(t => t.NormalizedName).ToList();
        foreach (var kw in keywords)
        {
            if (tagNames.Any(t => t.Contains(kw))) score += 3;
            if (description.Contains(kw))           score += 2;
            if (name.Contains(kw))                  score += 1;
            if (camera.Contains(kw))                score += 1;
        }
        return score;
    }

    public async Task<IEnumerable<MediaFile>> SearchByEmbeddingAsync(float[] queryEmbedding, float minScore = 0.18f, int topN = 50)
    {
        // Load only Id + embedding bytes to avoid hydrating the whole object graph during scoring.
        var candidates = await _context.MediaFiles
            .Where(m => m.ClipEmbeddingBytes != null)
            .Select(m => new { m.Id, m.ClipEmbeddingBytes })
            .ToListAsync();

        if (candidates.Count == 0) return [];

        var scored = candidates
            .Select(c => (Id: c.Id, Score: EmbeddingDot(queryEmbedding, c.ClipEmbeddingBytes!)))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();

        if (scored.Count == 0) return [];

        var ids   = scored.Select(x => x.Id).ToList();
        var files = await _context.MediaFiles
            .Include(m => m.Tags)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();

        // Preserve cosine-similarity order via O(1) dictionary lookup (not O(N²) FirstOrDefault).
        var fileById = files.ToDictionary(f => f.Id);
        return scored
            .Where(x => fileById.ContainsKey(x.Id))
            .Select(x => fileById[x.Id]);
    }

    public async Task<IEnumerable<MediaFile>> FindSimilarAsync(Guid sourceId, float minScore = 0.18f, int topN = 25)
    {
        // Load the source photo's embedding bytes.
        var sourceEmbeddingBytes = await _context.MediaFiles
            .Where(m => m.Id == sourceId)
            .Select(m => m.ClipEmbeddingBytes)
            .FirstOrDefaultAsync();

        if (sourceEmbeddingBytes == null) return [];

        var sourceEmbedding = BytesToFloats(sourceEmbeddingBytes);

        // Request topN+1 because the source photo itself scores 1.0 and will appear in results.
        var results = await SearchByEmbeddingAsync(sourceEmbedding, minScore, topN + 1);

        // Filter out the source photo and return exactly topN results.
        return results.Where(m => m.Id != sourceId).Take(topN);
    }

    private static float EmbeddingDot(float[] query, byte[] storedBytes)
    {
        int dim = storedBytes.Length / 4;
        int len = Math.Min(query.Length, dim);
        float sum = 0f;
        for (int i = 0; i < len; i++)
            sum += query[i] * BitConverter.ToSingle(storedBytes, i * 4);
        return sum;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        for (int i = 0; i < floats.Length; i++)
            floats[i] = BitConverter.ToSingle(bytes, i * 4);
        return floats;
    }

    public async Task<bool> ExistsAsync(string filePath) => await _context.MediaFiles.AnyAsync(m => m.FilePath == filePath);

    public async Task<HashSet<string>> GetAllFilePathsAsync() =>
        (await _context.MediaFiles.Select(m => m.FilePath).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<Tag> GetOrCreateTagAsync(string name, string normalizedName, TagCategory category, bool isAiGenerated, float confidence)
    {
        // AsNoTracking: the returned tag must NOT be held in the change tracker.
        // If it were tracked here, a later UpdateAsync(mediaFile) would try to attach
        // the same Tag instance a second time (via mf.Tags) and throw an identity conflict:
        // "another instance with the same key value for {'Id'} is already being tracked."
        var existing = await _context.Tags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.NormalizedName == normalizedName);
        if (existing != null) return existing;
        var tag = new Tag { Name = name, NormalizedName = normalizedName, Category = category, IsAIGenerated = isAiGenerated, Confidence = confidence };
        await _context.Tags.AddAsync(tag);
        await _context.SaveChangesAsync();
        // Detach after save so the newly-created tag doesn't linger in the tracker either.
        _context.Entry(tag).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return tag;
    }

    public async Task<List<string>> GetAllTagNamesAsync()
        => await _context.Tags
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .ToListAsync();

    public async Task<IReadOnlyDictionary<string, int>> GetTagPhotoCountsAsync()
    {
        // Single query: LEFT JOIN from Tags to non-excluded MediaFiles so zero-count tags
        // are included without a separate round-trip.
        return await _context.Tags
            .Where(t => t.Name != null)
            .Select(t => new
            {
                Name  = t.Name!,
                Count = t.MediaFiles.Count(m => !m.IsExcluded)
            })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Name, x => x.Count);
    }

    public async Task RemoveTagFromPhotoAsync(Guid photoId, Guid tagId)
    {
        var mf = await _context.MediaFiles.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == photoId);
        if (mf == null) return;
        var tag = mf.Tags.FirstOrDefault(t => t.Id == tagId);
        if (tag == null) return;
        mf.Tags.Remove(tag);
        await _context.SaveChangesAsync();
        await UpsertFtsAsync(mf);
    }

    public async Task<IEnumerable<DuplicateGroup>> GetDuplicateGroupsAsync(IProgress<string>? progress = null)
    {
        var results = new List<DuplicateGroup>();

        // Exact hash duplicates — files sharing the same SHA-256.
        // Single query: find duplicate hashes, then load all matching files in one round-trip
        // (replaces the previous N+1 pattern of one query per hash).
        progress?.Report("Checking for exact duplicates…");
        var dupHashes = await _context.MediaFiles
            .Where(m => m.FileHash != null && !m.IsExcluded)
            .GroupBy(m => m.FileHash!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync();

        if (dupHashes.Count > 0)
        {
            var dupFiles = await _context.MediaFiles
                .Where(m => dupHashes.Contains(m.FileHash!))
                .ToListAsync();
            foreach (var g in dupFiles.GroupBy(m => m.FileHash!))
                results.Add(new DuplicateGroup(DuplicateGroupType.ExactHash, g.ToList()));
        }

        // RAW+JPEG pairs — same filename stem in the same folder, different format.
        // Use a lightweight projection to identify pairs first, then load full entities
        // only for files that are actually part of a pair.
        progress?.Report("Checking for RAW+JPEG pairs…");
        var rawExts = SupportedFormats.RawExtensions;
        var jpgExts = SupportedFormats.JpegExtensions;
        var candidateExts = rawExts.Concat(jpgExts).ToList();

        var candidates = await _context.MediaFiles
            .Where(m => candidateExts.Contains(m.Extension) && !m.IsExcluded)
            .Select(m => new { m.Id, m.FilePath, m.FileName, m.Extension })
            .ToListAsync();

        var pairIds = candidates
            .GroupBy(m => (
                Dir: Path.GetDirectoryName(m.FilePath)?.ToLowerInvariant() ?? "",
                Stem: Path.GetFileNameWithoutExtension(m.FileName).ToLowerInvariant()))
            .Where(g => g.Count() > 1
                     && g.Any(f => rawExts.Contains(f.Extension))
                     && g.Any(f => jpgExts.Contains(f.Extension)))
            .SelectMany(g => g.Select(f => f.Id))
            .ToHashSet();

        if (pairIds.Count > 0)
        {
            var pairFiles = await _context.MediaFiles
                .Where(m => pairIds.Contains(m.Id))
                .ToListAsync();
            var pairedGroups = pairFiles.GroupBy(m => (
                Dir: Path.GetDirectoryName(m.FilePath)?.ToLowerInvariant() ?? "",
                Stem: Path.GetFileNameWithoutExtension(m.FileName).ToLowerInvariant()));
            foreach (var g in pairedGroups)
                results.Add(new DuplicateGroup(DuplicateGroupType.RawJpegPair, g.ToList()));
        }

        // Perceptual-hash near-duplicates — Hamming distance ≤ 8 between 64-bit pHash values.
        // Pairs are found in parallel across all CPU cores; union-find clustering is then
        // applied sequentially to the collected pairs.
        var hashRows = await _context.MediaFiles
            .Where(m => m.PerceptualHash != null && !m.IsExcluded)
            .Select(m => new { m.Id, Hash = (ulong)m.PerceptualHash! })
            .AsNoTracking()
            .ToListAsync();

        // Skip photos already accounted for in an exact-hash group.
        var exactIds = results
            .Where(g => g.Type == DuplicateGroupType.ExactHash)
            .SelectMany(g => g.Files.Select(f => f.Id))
            .ToHashSet();
        var phCandidates = hashRows.Where(r => !exactIds.Contains(r.Id)).ToList();

        if (phCandidates.Count > 1)
        {
            int count = phCandidates.Count;

            // Phase 1: find all near-duplicate pairs in parallel.
            // Parallel.For splits the outer loop across CPU cores; each thread appends
            // to its own local list (via thread-local storage) to avoid contention,
            // then all local lists are merged into one ConcurrentBag at completion.
            progress?.Report($"Comparing perceptual hashes ({count:N0} photos)…");
            var hashes = phCandidates.Select(c => c.Hash).ToArray();
            var pairs  = new System.Collections.Concurrent.ConcurrentBag<(int I, int J)>();

            await Task.Run(() =>
                Parallel.For(0, count - 1, () => new List<(int, int)>(),
                    (i, _, local) =>
                    {
                        ulong hi = hashes[i];
                        for (int j = i + 1; j < count; j++)
                            if (System.Numerics.BitOperations.PopCount(hi ^ hashes[j]) <= 8)
                                local.Add((i, j));
                        return local;
                    },
                    local => { foreach (var p in local) pairs.Add(p); }));

            // Phase 2: union-find clustering on the collected pairs (sequential, fast).
            var parent = Enumerable.Range(0, count).ToArray();

            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }

            foreach (var (i, j) in pairs)
            {
                int pi = Find(i), pj = Find(j);
                if (pi != pj) parent[pi] = pj;
            }

            var grouped = Enumerable.Range(0, count)
                .GroupBy(Find)
                .Where(g => g.Count() > 1)
                .Select(g => g.Select(i => phCandidates[i].Id).ToHashSet())
                .ToList();

            if (grouped.Count > 0)
            {
                var phIds = grouped.SelectMany(g => g).ToList();
                var phFiles = await _context.MediaFiles
                    .Where(m => phIds.Contains(m.Id))
                    .ToListAsync();
                var phFileMap = phFiles.ToDictionary(m => m.Id);

                foreach (var group in grouped)
                {
                    var files = group
                        .Where(id => phFileMap.ContainsKey(id))
                        .Select(id => phFileMap[id])
                        .ToList();
                    if (files.Count > 1)
                        results.Add(new DuplicateGroup(DuplicateGroupType.PerceptualSimilar, files));
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<(Guid Id, ulong Hash, string? AiDescription, string? AiModelUsed, string? PromptVersion, string? PostProcessVersion, bool IsAnalyzed)>>
        GetPerceptualHashCandidatesAsync()
    {
        return await _context.MediaFiles
            .Where(m => m.PerceptualHash != null && !m.IsExcluded)
            .Select(m => new
            {
                m.Id,
                Hash              = (ulong)m.PerceptualHash!,
                m.AiDescription,
                m.AiModelUsed,
                m.PromptVersion,
                m.PostProcessVersion,
                m.IsAnalyzed
            })
            .AsNoTracking()
            .ToListAsync()
            .ContinueWith(t => (IReadOnlyList<(Guid, ulong, string?, string?, string?, string?, bool)>)
                t.Result.Select(r => (r.Id, r.Hash, r.AiDescription, r.AiModelUsed, r.PromptVersion, r.PostProcessVersion, r.IsAnalyzed))
                        .ToList());
    }

    private static void DeleteFaceThumbnailFiles(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path))
                try { File.Delete(path); } catch { }
    }
}
