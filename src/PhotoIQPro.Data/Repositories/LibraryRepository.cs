using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

/// <summary>
/// Repository for Library and Album persistence and queries.
/// </summary>
/// <remarks>
/// Libraries are user-created containers for organizing photos. Each library can have multiple Albums.
/// This repository handles CRUD operations on both entities, plus the many-to-many Album↔Photo relationship.
///
/// Key patterns:
/// - Libraries and Albums are soft-deleted via cascade when deleted (with DeleteBehavior.Cascade)
/// - Album photo membership uses raw SQL for RemovePhotosFromAlbumAsync to avoid EF shadow entity tracking issues
/// - All operations are eager-loaded (Include) to minimize round-trips
/// </remarks>
public class LibraryRepository : ILibraryRepository
{
    private readonly PhotoIQContext _db;
    /// <summary>Initializes a new instance of the LibraryRepository.</summary>
    /// <param name="db">The PhotoIQContext for database access.</param>
    public LibraryRepository(PhotoIQContext db) => _db = db;

    /// <summary>Gets all libraries with their albums, sorted by library name.</summary>
    /// <remarks>Eagerly loads all albums to avoid N+1 queries.</remarks>
    /// <returns>A list of all Library entities with their Album collections populated.</returns>
    public async Task<IReadOnlyList<Library>> GetAllAsync()
        => await _db.Libraries
            .Include(l => l.Albums)
            .OrderBy(l => l.Name)
            .ToListAsync();

    /// <summary>Creates a new library with an optional description.</summary>
    /// <param name="name">The library name (e.g., "Vacations", "Family Events").</param>
    /// <param name="description">Optional description of the library's contents.</param>
    /// <returns>The created Library entity (with ID populated from the database).</returns>
    public async Task<Library> CreateLibraryAsync(string name, string? description = null)
    {
        var lib = new Library { Name = name, Description = description };
        _db.Libraries.Add(lib);
        await _db.SaveChangesAsync();
        return lib;
    }

    /// <summary>Creates a new album within a library.</summary>
    /// <param name="libraryId">The parent Library ID.</param>
    /// <param name="name">The album name (e.g., "Summer 2024").</param>
    /// <param name="description">Optional description of the album's contents.</param>
    /// <returns>The created Album entity (with ID populated from the database).</returns>
    public async Task<Album> CreateAlbumAsync(Guid libraryId, string name, string? description = null)
    {
        var album = new Album { LibraryId = libraryId, Name = name, Description = description };
        _db.Albums.Add(album);
        await _db.SaveChangesAsync();
        return album;
    }

    /// <summary>Renames an existing library.</summary>
    /// <remarks>Silently succeeds even if the library is not found (idempotent).</remarks>
    /// <param name="id">The Library ID to rename.</param>
    /// <param name="newName">The new library name.</param>
    public async Task RenameLibraryAsync(Guid id, string newName)
    {
        var lib = await _db.Libraries.FindAsync(id);
        if (lib == null) return;
        lib.Name = newName;
        await _db.SaveChangesAsync();
    }

    /// <summary>Renames an existing album.</summary>
    /// <remarks>Silently succeeds even if the album is not found (idempotent).</remarks>
    /// <param name="id">The Album ID to rename.</param>
    /// <param name="newName">The new album name.</param>
    public async Task RenameAlbumAsync(Guid id, string newName)
    {
        var album = await _db.Albums.FindAsync(id);
        if (album == null) return;
        album.Name = newName;
        await _db.SaveChangesAsync();
    }

    /// <summary>Permanently deletes a library (and cascades to delete all child albums).</summary>
    /// <remarks>Silently succeeds even if the library is not found (idempotent). Photos are not deleted; only the album associations are removed.</remarks>
    /// <param name="id">The Library ID to delete.</param>
    public async Task DeleteLibraryAsync(Guid id)
    {
        var lib = await _db.Libraries.FindAsync(id);
        if (lib == null) return;
        _db.Libraries.Remove(lib);
        await _db.SaveChangesAsync();
    }

    /// <summary>Permanently deletes an album (but retains all photos).</summary>
    /// <remarks>Silently succeeds even if the album is not found (idempotent). Photos remain in the library; only the album's photos association is removed.</remarks>
    /// <param name="id">The Album ID to delete.</param>
    public async Task DeleteAlbumAsync(Guid id)
    {
        var album = await _db.Albums.FindAsync(id);
        if (album == null) return;
        _db.Albums.Remove(album);
        await _db.SaveChangesAsync();
    }

    /// <summary>Adds photos to an album (skips photos already in the album).</summary>
    /// <remarks>This is a bulk operation that deduplicates: photos already in the album are silently skipped.</remarks>
    /// <param name="albumId">The Album ID to add photos to.</param>
    /// <param name="photoIds">IDs of the photos to add.</param>
    public async Task AddPhotosToAlbumAsync(Guid albumId, IReadOnlyList<Guid> photoIds)
    {
        var album = await _db.Albums
            .Include(a => a.MediaFiles)
            .FirstOrDefaultAsync(a => a.Id == albumId);
        if (album == null) return;

        var existingIds = album.MediaFiles.Select(m => m.Id).ToHashSet();
        var toAdd = await _db.MediaFiles
            .Where(m => photoIds.Contains(m.Id) && !existingIds.Contains(m.Id))
            .ToListAsync();

        foreach (var photo in toAdd)
            album.MediaFiles.Add(photo);

        await _db.SaveChangesAsync();
    }

    /// <summary>Removes photos from an album (does not delete the photos from the library).</summary>
    /// <remarks>
    /// Uses raw SQL to delete from the join table directly, avoiding EF Core change-tracker
    /// issues with shadow entities (the implicit photo_albums join table).
    /// </remarks>
    /// <param name="albumId">The Album ID to remove photos from.</param>
    /// <param name="photoIds">IDs of the photos to remove.</param>
    public async Task RemovePhotosFromAlbumAsync(Guid albumId, IReadOnlyList<Guid> photoIds)
    {
        if (photoIds.Count == 0) return;
        // Raw SQL avoids EF change-tracking issues with the shadow join entity.
        // Column names follow EF Core 8 convention: {NavigationName}Id.
        foreach (var photoId in photoIds)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM photo_albums WHERE AlbumsId = {albumId} AND MediaFilesId = {photoId}");
    }

    public async Task<IEnumerable<MediaFile>> GetPhotosByAlbumAsync(Guid albumId)
        => await _db.Albums
            .Where(a => a.Id == albumId)
            .SelectMany(a => a.MediaFiles)
            .Include(m => m.Tags)
            .OrderByDescending(m => m.DateTaken ?? m.DateImported)
            .ToListAsync();

    public async Task<Dictionary<Guid, int>> GetAlbumPhotoCountsAsync()
        => await _db.Albums
            .Select(a => new { a.Id, Count = a.MediaFiles.Count })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
}
