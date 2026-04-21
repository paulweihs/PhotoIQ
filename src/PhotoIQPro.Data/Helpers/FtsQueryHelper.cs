using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PhotoIQPro.Data.Helpers;

/// <summary>
/// Encapsulates FTS5 (Full-Text Search) query building and execution for the MediaFilesSearch virtual table.
/// Centralizes SQL patterns for DELETE and INSERT operations on the search index.
/// </summary>
/// <remarks>
/// All queries use parameterized FormattableString where possible (safe from SQL injection).
/// For bulk operations (DELETE IN), GUIDs are formatted directly as they contain only [0-9a-f-]
/// and are injection-safe by type.
/// </remarks>
public static class FtsQueryHelper
{
    /// <summary>
    /// Deletes a single MediaFile entry from the FTS index.
    /// </summary>
    /// <param name="database">The EF Core database facade for SQL execution.</param>
    /// <param name="mediaFileId">The MediaFile ID to delete from the index.</param>
    public static async Task DeleteSingleAsync(DatabaseFacade database, Guid mediaFileId)
    {
        await database.ExecuteSqlAsync(
            $"DELETE FROM MediaFilesSearch WHERE media_file_id = {mediaFileId.ToString()}");
    }

    /// <summary>
    /// Deletes multiple MediaFiles from the FTS index in chunked batches.
    /// Chunks to stay under SQLite's default SQLITE_LIMIT_VARIABLE_NUMBER (999).
    /// </summary>
    /// <remarks>
    /// GUIDs contain only [0-9a-f-] so formatting into a SQL IN list is injection-safe by type.
    /// FormattableString parameterization cannot expand variable-length collections, so we use
    /// ExecuteSqlRawAsync with formatted GUIDs (verified safe by type).
    /// </remarks>
    /// <param name="database">The EF Core database facade for SQL execution.</param>
    /// <param name="mediaFileIds">Collection of MediaFile IDs to delete.</param>
    public static async Task DeleteBatchAsync(DatabaseFacade database, IEnumerable<Guid> mediaFileIds)
    {
        foreach (var chunk in mediaFileIds.Chunk(900))
        {
            var idList = string.Join(",", chunk.Select(id => $"'{id}'"));
#pragma warning disable EF1002 // GUIDs are safe by type
            await database.ExecuteSqlRawAsync(
                $"DELETE FROM MediaFilesSearch WHERE media_file_id IN ({idList})");
#pragma warning restore EF1002
        }
    }

    /// <summary>
    /// Clears all entries from the FTS index (atomic operation).
    /// </summary>
    /// <remarks>
    /// DELETE is auto-committed and holds the write lock for only milliseconds.
    /// </remarks>
    /// <param name="database">The EF Core database facade for SQL execution.</param>
    public static async Task ClearAllAsync(DatabaseFacade database)
    {
        await database.ExecuteSqlRawAsync("DELETE FROM MediaFilesSearch");
    }

    /// <summary>
    /// Inserts or updates a MediaFile entry in the FTS index.
    /// Deletes any existing entry first, then inserts the new one.
    /// </summary>
    /// <remarks>
    /// Both operations use ExecuteSqlAsync(FormattableString), so all parameters are safe from injection
    /// even when description or tags contain quotes, newlines, or FTS5 query operators.
    /// </remarks>
    /// <param name="database">The EF Core database facade for SQL execution.</param>
    /// <param name="mediaFileId">The MediaFile ID to index.</param>
    /// <param name="description">User or AI-generated description (searchable text).</param>
    /// <param name="tagText">Comma-separated tag names for indexing.</param>
    /// <param name="filename">File name without extension for search.</param>
    /// <param name="camera">Camera model (EXIF metadata).</param>
    /// <param name="dateText">Formatted date text including year, month, day, holidays.</param>
    /// <param name="folder">Folder path for directory-based search.</param>
    public static async Task UpsertAsync(
        DatabaseFacade database,
        Guid mediaFileId,
        string description,
        string tagText,
        string filename,
        string camera,
        string dateText,
        string folder)
    {
        // Delete existing entry (if any) to ensure clean upsert
        await database.ExecuteSqlAsync(
            $"DELETE FROM MediaFilesSearch WHERE media_file_id = {mediaFileId}");

        // Insert new entry with all searchable fields
        await database.ExecuteSqlAsync(
            $"INSERT INTO MediaFilesSearch(media_file_id, description, tags, filename, camera, date_text, folder) VALUES ({mediaFileId},{description},{tagText},{filename},{camera},{dateText},{folder})");
    }
}
