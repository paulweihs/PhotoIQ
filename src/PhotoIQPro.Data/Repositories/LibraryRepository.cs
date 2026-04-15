using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

public class LibraryRepository : ILibraryRepository
{
    private readonly PhotoIQContext _db;

    public LibraryRepository(PhotoIQContext db) => _db = db;

    public async Task<IReadOnlyList<Library>> GetAllAsync()
        => await _db.Libraries
            .Include(l => l.Albums)
            .OrderBy(l => l.Name)
            .ToListAsync();

    public async Task<Library> CreateLibraryAsync(string name, string? description = null)
    {
        var lib = new Library { Name = name, Description = description };
        _db.Libraries.Add(lib);
        await _db.SaveChangesAsync();
        return lib;
    }

    public async Task<Album> CreateAlbumAsync(Guid libraryId, string name, string? description = null)
    {
        var album = new Album { LibraryId = libraryId, Name = name, Description = description };
        _db.Albums.Add(album);
        await _db.SaveChangesAsync();
        return album;
    }

    public async Task RenameLibraryAsync(Guid id, string newName)
    {
        var lib = await _db.Libraries.FindAsync(id);
        if (lib == null) return;
        lib.Name = newName;
        await _db.SaveChangesAsync();
    }

    public async Task RenameAlbumAsync(Guid id, string newName)
    {
        var album = await _db.Albums.FindAsync(id);
        if (album == null) return;
        album.Name = newName;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteLibraryAsync(Guid id)
    {
        var lib = await _db.Libraries.FindAsync(id);
        if (lib == null) return;
        _db.Libraries.Remove(lib);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAlbumAsync(Guid id)
    {
        var album = await _db.Albums.FindAsync(id);
        if (album == null) return;
        _db.Albums.Remove(album);
        await _db.SaveChangesAsync();
    }

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
