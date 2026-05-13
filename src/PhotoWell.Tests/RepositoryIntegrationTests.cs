using System.IO;
using Microsoft.EntityFrameworkCore;
using PhotoWell.Core.Models;
using PhotoWell.Data;
using PhotoWell.Data.Repositories;
using Xunit;

namespace PhotoWell.Tests;

// ── Shared SQLite fixture ────────────────────────────────────────────────────

/// <summary>
/// Creates a fresh SQLite database (unique temp file) per test-class instance.
/// Includes the FTS5 virtual table required by MediaFileRepository's UpsertFtsAsync.
/// </summary>
public abstract class RepositoryTestBase : IDisposable
{
    private readonly string _dbPath;
    protected readonly PhotoWellContext Db;

    protected RepositoryTestBase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"photowell_test_{Guid.NewGuid():N}.db");
        Db = new PhotoWellContext(
            new DbContextOptionsBuilder<PhotoWellContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options);
        Db.Database.EnsureCreated();

        // FTS5 virtual table is created by migrations in production; EnsureCreated() does not
        // run migrations. Create it manually so UpsertFtsAsync (called by Add/Update/Delete)
        // can write to it without throwing.
        Db.Database.ExecuteSqlRaw(@"
            CREATE VIRTUAL TABLE IF NOT EXISTS MediaFilesSearch USING fts5(
                media_file_id UNINDEXED,
                description,
                tags,
                filename,
                camera,
                date_text,
                folder
            )");
    }

    public void Dispose()
    {
        Db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    /// <summary>Minimal valid MediaFile for use in tests.</summary>
    protected static MediaFile Photo(
        string path,
        string? hash = null,
        bool favorite = false,
        AnalysisStatus status = AnalysisStatus.Pending) => new()
    {
        FilePath         = path,
        FileName         = Path.GetFileName(path),
        Extension        = Path.GetExtension(path),
        FileSize         = 500_000,
        FileHash         = hash ?? Guid.NewGuid().ToString("N"),
        IsFavorite       = favorite,
        AnalysisStatus   = status,
        DateImported     = DateTime.UtcNow,
    };
}

// ── MediaFileRepository integration tests ────────────────────────────────────

public class MediaFileRepositoryTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public MediaFileRepositoryTests() => _repo = new MediaFileRepository(Db);

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_PersistsPhotoWithCorrectPath()
    {
        var photo = Photo("/photos/sunset.jpg");
        await _repo.AddAsync(photo);

        var loaded = await Db.MediaFiles.FindAsync(photo.Id);
        Assert.NotNull(loaded);
        Assert.Equal("/photos/sunset.jpg", loaded.FilePath);
    }

    [Fact]
    public async Task AddAsync_AssignsNonEmptyId()
    {
        var photo = Photo("/photos/id_test.jpg");
        var result = await _repo.AddAsync(photo);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task AddAsync_DuplicatePath_ThrowsDbUpdateException()
    {
        await _repo.AddAsync(Photo("/dup/photo.jpg"));
        // FilePath has a unique index — second insert must fail
        await Assert.ThrowsAsync<DbUpdateException>(
            () => _repo.AddAsync(Photo("/dup/photo.jpg")));
    }

    // ── GetByPathAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByPathAsync_KnownPath_ReturnsMatchingFile()
    {
        var photo = Photo("/gallery/img.jpg");
        await _repo.AddAsync(photo);

        var found = await _repo.GetByPathAsync("/gallery/img.jpg");
        Assert.NotNull(found);
        Assert.Equal(photo.Id, found.Id);
    }

    [Fact]
    public async Task GetByPathAsync_UnknownPath_ReturnsNull()
        => Assert.Null(await _repo.GetByPathAsync("/missing/file.jpg"));

    // ── GetByHashAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByHashAsync_KnownHash_ReturnsMatchingFile()
    {
        const string hash = "deadbeef001122334455aabbccdd";
        await _repo.AddAsync(Photo("/hash/img.jpg", hash: hash));

        var found = await _repo.GetByHashAsync(hash);
        Assert.NotNull(found);
        Assert.Equal(hash, found.FileHash);
    }

    [Fact]
    public async Task GetByHashAsync_UnknownHash_ReturnsNull()
        => Assert.Null(await _repo.GetByHashAsync("no-such-hash-00000000"));

    // ── ExistsAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_KnownPath_ReturnsTrue()
    {
        await _repo.AddAsync(Photo("/exist/photo.jpg"));
        Assert.True(await _repo.ExistsAsync("/exist/photo.jpg"));
    }

    [Fact]
    public async Task ExistsAsync_UnknownPath_ReturnsFalse()
        => Assert.False(await _repo.ExistsAsync("/no/such/file.jpg"));

    // ── CountAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountAsync_ExcludesPhotosWithIsExcludedTrue()
    {
        var active   = Photo("/count/active.jpg");
        var excluded = Photo("/count/excluded.jpg");
        excluded.IsExcluded = true;

        await _repo.AddAsync(active);
        await _repo.AddAsync(excluded);

        Assert.Equal(1, await _repo.CountAsync());
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesFileFromDatabase()
    {
        var photo = Photo("/del/pic.jpg");
        await _repo.AddAsync(photo);

        await _repo.DeleteAsync(photo.Id);

        Assert.Null(await Db.MediaFiles.FindAsync(photo.Id));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
        => await _repo.DeleteAsync(Guid.NewGuid()); // silent no-op

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_PersistsChangedFields()
    {
        var photo = Photo("/upd/img.jpg");
        await _repo.AddAsync(photo);

        photo.IsFavorite     = true;
        photo.AiDescription  = "A sunset over the mountains.";
        photo.AnalysisStatus = AnalysisStatus.Complete;
        await _repo.UpdateAsync(photo);

        // Detach and reload to confirm DB round-trip
        Db.ChangeTracker.Clear();
        var loaded = await Db.MediaFiles.FindAsync(photo.Id);
        Assert.True(loaded?.IsFavorite);
        Assert.Equal("A sunset over the mountains.", loaded?.AiDescription);
        Assert.Equal(AnalysisStatus.Complete, loaded?.AnalysisStatus);
    }

    // ── GetFavoritesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetFavoritesAsync_ReturnsOnlyIsFavoriteTrueFiles()
    {
        await _repo.AddAsync(Photo("/fav/a.jpg", favorite: true));
        await _repo.AddAsync(Photo("/fav/b.jpg", favorite: false));

        var favs = (await _repo.GetFavoritesAsync()).ToList();
        Assert.Single(favs);
        Assert.True(favs[0].IsFavorite);
    }

    [Fact]
    public async Task GetFavoritesAsync_DoesNotReturnExcludedFavorites()
    {
        var photo = Photo("/fav/excl.jpg", favorite: true);
        photo.IsExcluded = true;
        await _repo.AddAsync(photo);

        Assert.Empty(await _repo.GetFavoritesAsync());
    }

    // ── GetUnanalyzedAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnanalyzedAsync_ReturnsOnlyIsAnalyzedFalseFiles()
    {
        var unanalyzed = Photo("/un/a.jpg"); // IsAnalyzed = false by default
        var analyzed   = Photo("/un/b.jpg");
        analyzed.IsAnalyzed = true;

        await _repo.AddAsync(unanalyzed);
        await _repo.AddAsync(analyzed);

        var results = (await _repo.GetUnanalyzedAsync()).ToList();
        Assert.Single(results);
        Assert.Equal(unanalyzed.Id, results[0].Id);
    }

    [Fact]
    public async Task GetUnanalyzedAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _repo.AddAsync(Photo($"/un/lim{i}.jpg"));

        var results = (await _repo.GetUnanalyzedAsync(limit: 3)).ToList();
        Assert.Equal(3, results.Count);
    }

    // ── GetVisionPendingAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetVisionPendingAsync_ReturnsOnlyVisionPendingStatus()
    {
        await _repo.AddAsync(Photo("/vp/a.jpg", status: AnalysisStatus.VisionPending));
        await _repo.AddAsync(Photo("/vp/b.jpg", status: AnalysisStatus.Complete));
        await _repo.AddAsync(Photo("/vp/c.jpg", status: AnalysisStatus.Pending));

        var results = (await _repo.GetVisionPendingAsync()).ToList();
        Assert.Single(results);
        Assert.Equal(AnalysisStatus.VisionPending, results[0].AnalysisStatus);
    }

    // ── GetAllFilePathsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAllFilePathsAsync_ReturnsHashSetOfAllPaths()
    {
        await _repo.AddAsync(Photo("/paths/a.jpg"));
        await _repo.AddAsync(Photo("/paths/b.jpg"));

        var paths = await _repo.GetAllFilePathsAsync();

        Assert.Contains("/paths/a.jpg", paths);
        Assert.Contains("/paths/b.jpg", paths);
    }

    // ── GetByIdsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdsAsync_ReturnsBatchOfMatchingPhotos()
    {
        var a = Photo("/batch/a.jpg");
        var b = Photo("/batch/b.jpg");
        var c = Photo("/batch/c.jpg");
        await _repo.AddAsync(a);
        await _repo.AddAsync(b);
        await _repo.AddAsync(c);

        var results = await _repo.GetByIdsAsync([a.Id, c.Id]);
        var ids = results.Select(m => m.Id).ToHashSet();

        Assert.Equal(2, results.Count);
        Assert.Contains(a.Id, ids);
        Assert.Contains(c.Id, ids);
        Assert.DoesNotContain(b.Id, ids);
    }

    // ── GetNearbyDateAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetNearbyDateAsync_FindsPhotoWithDateTakenInWindow()
    {
        var anchor = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var inWindow  = Photo("/near/in.jpg");
        var outWindow = Photo("/near/out.jpg");
        inWindow.DateTaken  = anchor.AddMinutes(30);   // inside 60-min window
        outWindow.DateTaken = anchor.AddMinutes(120);  // outside window

        // Add directly to avoid FTS side-effects on photos we're not testing via the repo
        Db.MediaFiles.AddRange(inWindow, outWindow);
        await Db.SaveChangesAsync();

        var results = await _repo.GetNearbyDateAsync(anchor, windowMinutes: 60, excludeId: Guid.Empty);
        var ids = results.Select(m => m.Id).ToHashSet();

        Assert.Contains(inWindow.Id, ids);
        Assert.DoesNotContain(outWindow.Id, ids);
    }

    [Fact]
    public async Task GetNearbyDateAsync_FallsBackToDateImported_WhenDateTakenIsNull()
    {
        // Reproduces the Facebook-photo fix: no EXIF → use DateImported as the date proxy.
        var anchor  = new DateTime(2024, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var fbPhoto = Photo("/near/fb.jpg");
        fbPhoto.DateTaken    = null;                       // no EXIF date
        fbPhoto.DateImported = anchor.AddMinutes(20);      // within a 60-min window

        Db.MediaFiles.Add(fbPhoto);
        await Db.SaveChangesAsync();

        var results = await _repo.GetNearbyDateAsync(anchor, windowMinutes: 60, excludeId: Guid.Empty);
        Assert.Contains(results, m => m.Id == fbPhoto.Id);
    }

    [Fact]
    public async Task GetNearbyDateAsync_PhotoWithDateTakenOutsideWindow_NotReturned()
    {
        var anchor  = new DateTime(2024, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var farAway = Photo("/near/far.jpg");
        farAway.DateTaken = anchor.AddHours(5); // well outside any reasonable window

        Db.MediaFiles.Add(farAway);
        await Db.SaveChangesAsync();

        var results = await _repo.GetNearbyDateAsync(anchor, windowMinutes: 60, excludeId: Guid.Empty);
        Assert.DoesNotContain(results, m => m.Id == farAway.Id);
    }

    [Fact]
    public async Task GetNearbyDateAsync_ExcludesAnchorId()
    {
        var anchor      = new DateTime(2024, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var anchorPhoto = Photo("/near/anchor.jpg");
        anchorPhoto.DateTaken = anchor;

        Db.MediaFiles.Add(anchorPhoto);
        await Db.SaveChangesAsync();

        var results = await _repo.GetNearbyDateAsync(anchor, windowMinutes: 60, excludeId: anchorPhoto.Id);
        Assert.DoesNotContain(results, m => m.Id == anchorPhoto.Id);
    }
}

// ── LibraryRepository integration tests ─────────────────────────────────────

public class LibraryRepositoryTests : RepositoryTestBase
{
    private readonly LibraryRepository _repo;
    public LibraryRepositoryTests() => _repo = new LibraryRepository(Db);

    /// <summary>Add a MediaFile directly (no FTS) for use as an album photo target.</summary>
    private async Task<MediaFile> AddPhotoDirectAsync(string path)
    {
        var photo = new MediaFile
        {
            FilePath     = path,
            FileName     = Path.GetFileName(path),
            Extension    = Path.GetExtension(path),
            FileSize     = 500_000,
            FileHash     = Guid.NewGuid().ToString("N"),
            DateImported = DateTime.UtcNow,
        };
        Db.MediaFiles.Add(photo);
        await Db.SaveChangesAsync();
        return photo;
    }

    // ── CreateLibraryAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLibraryAsync_PersistsLibraryWithCorrectName()
    {
        var lib = await _repo.CreateLibraryAsync("Holidays");
        Assert.NotEqual(Guid.Empty, lib.Id);
        Assert.Equal("Holidays", lib.Name);
        Assert.NotNull(await Db.Libraries.FindAsync(lib.Id));
    }

    [Fact]
    public async Task CreateLibraryAsync_WithDescription_PersistsDescription()
    {
        var lib = await _repo.CreateLibraryAsync("Work", "Company photos");
        Assert.Equal("Company photos", lib.Description);
    }

    // ── GetAllAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_ReturnsLibrariesOrderedByName()
    {
        await _repo.CreateLibraryAsync("Zebra");
        await _repo.CreateLibraryAsync("Apple");
        await _repo.CreateLibraryAsync("Mango");

        var libs  = await _repo.GetAllAsync();
        var names = libs.Select(l => l.Name).ToList();

        Assert.Equal(["Apple", "Mango", "Zebra"], names);
    }

    [Fact]
    public async Task GetAllAsync_IncludesAlbums()
    {
        var lib = await _repo.CreateLibraryAsync("WithAlbums");
        await _repo.CreateAlbumAsync(lib.Id, "Summer");

        var all = await _repo.GetAllAsync();
        Assert.Single(all.First(l => l.Id == lib.Id).Albums);
    }

    // ── CreateAlbumAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAlbumAsync_LinksAlbumToLibrary()
    {
        var lib   = await _repo.CreateLibraryAsync("Wedding");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Ceremony");

        Assert.Equal(lib.Id,     album.LibraryId);
        Assert.Equal("Ceremony", album.Name);
    }

    // ── RenameLibraryAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task RenameLibraryAsync_UpdatesNameInDatabase()
    {
        var lib = await _repo.CreateLibraryAsync("OldName");
        await _repo.RenameLibraryAsync(lib.Id, "NewName");

        Db.ChangeTracker.Clear();
        var updated = await Db.Libraries.FindAsync(lib.Id);
        Assert.Equal("NewName", updated?.Name);
    }

    [Fact]
    public async Task RenameLibraryAsync_NonExistentId_DoesNotThrow()
        => await _repo.RenameLibraryAsync(Guid.NewGuid(), "Whatever");

    // ── RenameAlbumAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RenameAlbumAsync_UpdatesAlbumNameInDatabase()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "OldAlbum");

        await _repo.RenameAlbumAsync(album.Id, "NewAlbum");

        Db.ChangeTracker.Clear();
        var updated = await Db.Albums.FindAsync(album.Id);
        Assert.Equal("NewAlbum", updated?.Name);
    }

    // ── DeleteAlbumAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAlbumAsync_RemovesAlbumFromDatabase()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "ToDelete");

        await _repo.DeleteAlbumAsync(album.Id);

        Assert.Null(await Db.Albums.FindAsync(album.Id));
    }

    [Fact]
    public async Task DeleteAlbumAsync_LibraryRemainsAfterAlbumDeleted()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");

        await _repo.DeleteAlbumAsync(album.Id);

        Assert.NotNull(await Db.Libraries.FindAsync(lib.Id));
    }

    // ── DeleteLibraryAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteLibraryAsync_CascadesToChildAlbums()
    {
        var lib   = await _repo.CreateLibraryAsync("CascadeLib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "CascadeAlbum");

        await _repo.DeleteLibraryAsync(lib.Id);

        Assert.Null(await Db.Libraries.FindAsync(lib.Id));
        Assert.Null(await Db.Albums.FindAsync(album.Id));
    }

    // ── AddPhotosToAlbumAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task AddPhotosToAlbumAsync_LinksPhotosToAlbum()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var photo = await AddPhotoDirectAsync("/add/photo.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [photo.Id]);

        var albumPhotos = (await _repo.GetPhotosByAlbumAsync(album.Id)).ToList();
        Assert.Single(albumPhotos);
        Assert.Equal(photo.Id, albumPhotos[0].Id);
    }

    [Fact]
    public async Task AddPhotosToAlbumAsync_IsIdempotent_NoDuplicateLinks()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var photo = await AddPhotoDirectAsync("/idem/photo.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [photo.Id]);
        await _repo.AddPhotosToAlbumAsync(album.Id, [photo.Id]); // second add — already linked

        var albumPhotos = await _repo.GetPhotosByAlbumAsync(album.Id);
        Assert.Single(albumPhotos);
    }

    [Fact]
    public async Task AddPhotosToAlbumAsync_MultiplePhotos_AllLinked()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var p1    = await AddPhotoDirectAsync("/multi/p1.jpg");
        var p2    = await AddPhotoDirectAsync("/multi/p2.jpg");
        var p3    = await AddPhotoDirectAsync("/multi/p3.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [p1.Id, p2.Id, p3.Id]);

        var albumPhotos = await _repo.GetPhotosByAlbumAsync(album.Id);
        Assert.Equal(3, albumPhotos.Count());
    }

    // ── RemovePhotosFromAlbumAsync ────────────────────────────────────────────

    [Fact]
    public async Task RemovePhotosFromAlbumAsync_UnlinksPhotoFromAlbum()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var photo = await AddPhotoDirectAsync("/rem/photo.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [photo.Id]);
        await _repo.RemovePhotosFromAlbumAsync(album.Id, [photo.Id]);

        Assert.Empty(await _repo.GetPhotosByAlbumAsync(album.Id));
    }

    [Fact]
    public async Task RemovePhotosFromAlbumAsync_PhotoStillExistsInLibraryAfterRemoval()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var photo = await AddPhotoDirectAsync("/rem/stays.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [photo.Id]);
        await _repo.RemovePhotosFromAlbumAsync(album.Id, [photo.Id]);

        // The photo is unlinked from the album, but NOT deleted from the library
        Assert.NotNull(await Db.MediaFiles.FindAsync(photo.Id));
    }

    [Fact]
    public async Task RemovePhotosFromAlbumAsync_RemovesOneButNotOther()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        var keep  = await AddPhotoDirectAsync("/rem/keep.jpg");
        var drop  = await AddPhotoDirectAsync("/rem/drop.jpg");

        await _repo.AddPhotosToAlbumAsync(album.Id, [keep.Id, drop.Id]);
        await _repo.RemovePhotosFromAlbumAsync(album.Id, [drop.Id]);

        var albumPhotos = (await _repo.GetPhotosByAlbumAsync(album.Id)).ToList();
        Assert.Single(albumPhotos);
        Assert.Equal(keep.Id, albumPhotos[0].Id);
    }

    [Fact]
    public async Task RemovePhotosFromAlbumAsync_EmptyList_DoesNotThrow()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Album");
        await _repo.RemovePhotosFromAlbumAsync(album.Id, []); // early-return path
    }

    // ── GetPhotosByAlbumAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetPhotosByAlbumAsync_ReturnsOnlyPhotosInThatAlbum()
    {
        var lib    = await _repo.CreateLibraryAsync("Lib");
        var albumA = await _repo.CreateAlbumAsync(lib.Id, "AlbumA");
        var albumB = await _repo.CreateAlbumAsync(lib.Id, "AlbumB");
        var photoA = await AddPhotoDirectAsync("/ab/a.jpg");
        var photoB = await AddPhotoDirectAsync("/ab/b.jpg");

        await _repo.AddPhotosToAlbumAsync(albumA.Id, [photoA.Id]);
        await _repo.AddPhotosToAlbumAsync(albumB.Id, [photoB.Id]);

        var inA = (await _repo.GetPhotosByAlbumAsync(albumA.Id)).ToList();
        Assert.Single(inA);
        Assert.Equal(photoA.Id, inA[0].Id);
    }

    [Fact]
    public async Task GetPhotosByAlbumAsync_EmptyAlbum_ReturnsEmpty()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Empty");

        Assert.Empty(await _repo.GetPhotosByAlbumAsync(album.Id));
    }

    // ── GetAlbumPhotoCountsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetAlbumPhotoCountsAsync_ReturnsCorrectCountsPerAlbum()
    {
        var lib    = await _repo.CreateLibraryAsync("Lib");
        var album1 = await _repo.CreateAlbumAsync(lib.Id, "Album1");
        var album2 = await _repo.CreateAlbumAsync(lib.Id, "Album2");
        var p1     = await AddPhotoDirectAsync("/cnt/p1.jpg");
        var p2     = await AddPhotoDirectAsync("/cnt/p2.jpg");
        var p3     = await AddPhotoDirectAsync("/cnt/p3.jpg");

        await _repo.AddPhotosToAlbumAsync(album1.Id, [p1.Id, p2.Id]);
        await _repo.AddPhotosToAlbumAsync(album2.Id, [p3.Id]);

        var counts = await _repo.GetAlbumPhotoCountsAsync();

        Assert.Equal(2, counts[album1.Id]);
        Assert.Equal(1, counts[album2.Id]);
    }

    [Fact]
    public async Task GetAlbumPhotoCountsAsync_EmptyAlbum_CountIsZero()
    {
        var lib   = await _repo.CreateLibraryAsync("Lib");
        var album = await _repo.CreateAlbumAsync(lib.Id, "Empty");

        var counts = await _repo.GetAlbumPhotoCountsAsync();
        Assert.Equal(0, counts[album.Id]);
    }
}
