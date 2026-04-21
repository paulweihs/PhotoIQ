using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Data.Helpers;

namespace PhotoIQPro.Data.Repositories;

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
    private readonly PhotoIQContext _context;
    /// <summary>Initializes a new instance of the MediaFileRepository.</summary>
    /// <param name="context">The PhotoIQContext for database access.</param>
    public MediaFileRepository(PhotoIQContext context) => _context = context;

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
        await _context.MediaFiles.AddAsync(entity);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Clear the change tracker so the failed entity (left in Added state by EF Core)
            // does not contaminate subsequent SaveChangesAsync calls on this DbContext instance.
            // Without this, every following write on the same context would also fail.
            _context.ChangeTracker.Clear();
            throw;
        }
        await UpsertFtsAsync(entity);
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
        _context.MediaFiles.Update(entity);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            throw;
        }
        await UpsertFtsAsync(entity);
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
        var now = DateTime.UtcNow;
        foreach (var f in files)
        {
            f.DateModified = now;
            _context.MediaFiles.Update(f);
        }
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            throw;
        }
        foreach (var f in files)
            await UpsertFtsAsync(f);
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
        var e = await _context.MediaFiles.FindAsync(id);
        if (e == null) return;
        _context.MediaFiles.Remove(e);
        await _context.SaveChangesAsync();
        await FtsQueryHelper.DeleteSingleAsync(_context.Database, id);
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

        _context.MediaFiles.RemoveRange(toDelete);
        await _context.SaveChangesAsync();

        // Batch-delete from the FTS index using helper (handles chunking and parameterization).
        await FtsQueryHelper.DeleteBatchAsync(_context.Database, toDelete.Select(mf => mf.Id));

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

    public async Task<IReadOnlyList<Face>> GetFacesForPhotoAsync(Guid mediaFileId)
        => await _context.Faces
            .Include(f => f.Person)
            .Where(f => f.MediaFileId == mediaFileId)
            .ToListAsync();

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

    public async Task<IEnumerable<MediaFile>> GetOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion)
        => await _context.MediaFiles
            .Where(m => !m.IsExcluded
                     && m.IsAnalyzed
                     && m.AiDescription != null
                     && (m.AiModelUsed != currentModel
                         || m.PromptVersion == null
                         || m.PromptVersion != currentPromptVersion))
            .ToListAsync();

    public async Task<int> CountOutdatedDescriptionsAsync(string currentModel, string currentPromptVersion)
        => await _context.MediaFiles
            .CountAsync(m => !m.IsExcluded
                          && m.IsAnalyzed
                          && m.AiDescription != null
                          && (m.AiModelUsed != currentModel
                              || m.PromptVersion == null
                              || m.PromptVersion != currentPromptVersion));

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
    public async Task RebuildFtsIndexAsync()
    {
        // DELETE is a single auto-committed statement — holds the write lock for milliseconds.
        // Previously this ran inside one Serializable transaction that held the lock for the
        // entire rebuild (minutes on large libraries), blocking every other writer with SQLITE_BUSY.
        await FtsQueryHelper.ClearAllAsync(_context.Database);

        const int chunkSize = 1000;
        int offset = 0;
        while (true)
        {
            var chunk = await _context.MediaFiles
                .Include(m => m.Tags)
                .AsNoTracking()
                .OrderBy(m => m.Id)
                .Skip(offset)
                .Take(chunkSize)
                .ToListAsync();

            if (chunk.Count == 0) break;

            foreach (var m in chunk)
                await UpsertFtsAsync(m); // two auto-committed SQL statements — lock held for microseconds each

            if (chunk.Count < chunkSize) break;
            offset += chunkSize;

            // Yield between chunks so other writers (vision worker, user commands) can
            // acquire write transactions without waiting.
            await Task.Delay(10);
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

        var tagText     = string.Join(" ", tags.Select(t => t.Name));
        var filename    = Path.GetFileNameWithoutExtension(m.FileName);
        var camera      = $"{m.CameraMake ?? ""} {m.CameraModel ?? ""}".Trim();
        var dateText    = BuildDateText(m.DateTaken);
        var folder      = Path.GetFileName(Path.GetDirectoryName(m.FilePath) ?? "") ?? "";

        // ADR-003: user_description takes display priority; index both so either is searchable.
        var description = !string.IsNullOrEmpty(m.UserDescription)
            ? m.UserDescription
            : m.AiDescription ?? "";

        // Upsert into FTS index (handles both delete and insert with parameterized safety).
        await FtsQueryHelper.UpsertAsync(_context.Database, m.Id, description, tagText, filename, camera, dateText, folder);
    }

    /// <summary>
    /// Converts a user query into a safe FTS5 MATCH expression.
    /// Each token gets a prefix wildcard (*) so partial words match.
    /// Multiple tokens use FTS5 AND semantics (space-separated).
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        // Strip FTS5 special chars that would cause syntax errors.
        var clean = Regex.Replace(query, @"[""()\[\]{}\*\+\-\^\!\&\|:,\.]", " ");
        var tokens = clean
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Select(t => t + "*")   // prefix match: "watermelon*" also hits "watermelons"
            .ToArray();
        return string.Join(" ", tokens);  // FTS5: space = AND
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
        var keywords = query
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 2)
            .Distinct()
            .Take(10)
            .ToArray();

        if (keywords.Length == 0)
            return await GetAllAsync();

        var matchIds = new HashSet<Guid>();
        foreach (var kw in keywords)
        {
            var ids = await _context.MediaFiles
                .Where(m =>
                    EF.Functions.Like(m.FileName.ToLower(), $"%{kw}%") ||
                    (m.CameraModel  != null && EF.Functions.Like(m.CameraModel.ToLower(),  $"%{kw}%")) ||
                    (m.CameraMake   != null && EF.Functions.Like(m.CameraMake.ToLower(),   $"%{kw}%")) ||
                    m.Tags.Any(t => EF.Functions.Like(t.NormalizedName, $"%{kw}%")))
                .Select(m => m.Id)
                .ToListAsync();
            foreach (var id in ids) matchIds.Add(id);
        }

        if (matchIds.Count == 0) return [];

        var candidates = await _context.MediaFiles
            .Include(m => m.Tags)
            .Where(m => matchIds.Contains(m.Id))
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
        var name     = m.FileName.ToLowerInvariant();
        var camera   = $"{m.CameraMake} {m.CameraModel}".ToLowerInvariant();
        var tagNames = m.Tags.Select(t => t.NormalizedName).ToList();
        foreach (var kw in keywords)
        {
            if (tagNames.Any(t => t.Contains(kw))) score += 3;
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

    private static float EmbeddingDot(float[] query, byte[] storedBytes)
    {
        int dim = storedBytes.Length / 4;
        int len = Math.Min(query.Length, dim);
        float sum = 0f;
        for (int i = 0; i < len; i++)
            sum += query[i] * BitConverter.ToSingle(storedBytes, i * 4);
        return sum;
    }

    public async Task<bool> ExistsAsync(string filePath) => await _context.MediaFiles.AnyAsync(m => m.FilePath == filePath);

    public async Task<HashSet<string>> GetAllFilePathsAsync() =>
        (await _context.MediaFiles.Select(m => m.FilePath).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<Tag> GetOrCreateTagAsync(string name, string normalizedName, TagCategory category, bool isAiGenerated, float confidence)
    {
        var existing = await _context.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalizedName);
        if (existing != null) return existing;
        var tag = new Tag { Name = name, NormalizedName = normalizedName, Category = category, IsAIGenerated = isAiGenerated, Confidence = confidence };
        await _context.Tags.AddAsync(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    public async Task<List<string>> GetAllTagNamesAsync()
        => await _context.Tags
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .ToListAsync();

    public async Task<IReadOnlyDictionary<string, int>> GetTagPhotoCountsAsync()
    {
        // Query 1: single JOIN + GROUP BY — one row per tag with non-excluded photos.
        var counts = await _context.MediaFiles
            .Where(m => !m.IsExcluded)
            .SelectMany(m => m.Tags)
            .Where(t => t.Name != null)
            .GroupBy(t => t.Name!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .AsNoTracking()
            .ToListAsync();

        // Query 2: all tag names (cheap — no join, ensures zero-count tags are included).
        var allTagNames = await _context.Tags
            .Where(t => t.Name != null)
            .Select(t => t.Name!)
            .AsNoTracking()
            .ToListAsync();

        var countMap = counts.ToDictionary(x => x.Name, x => x.Count);
        return allTagNames.ToDictionary(name => name, name => countMap.GetValueOrDefault(name, 0));
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

    public async Task<IEnumerable<DuplicateGroup>> GetDuplicateGroupsAsync()
    {
        var results = new List<DuplicateGroup>();

        // Exact hash duplicates — files sharing the same SHA-256.
        // Single query: find duplicate hashes, then load all matching files in one round-trip
        // (replaces the previous N+1 pattern of one query per hash).
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

        return results;
    }
}
