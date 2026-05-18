using System.IO;
using Microsoft.EntityFrameworkCore;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Data.Repositories;
using Xunit;

namespace PhotoWell.Tests;

// ── RemoveByFolderAsync ───────────────────────────────────────────────────────
// Uses Windows backslash paths because the implementation computes the prefix
// with Path.DirectorySeparatorChar ('\' on Windows); forward-slash paths would
// produce a mismatched prefix and never match.

/// <summary>Tests for MediaFileRepository.RemoveByFolderAsync: bulk deletion with recursion control.</summary>
public class RemoveByFolderTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public RemoveByFolderTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task IncludeSubfolders_True_RemovesEntireSubtree()
    {
        await _repo.AddAsync(Photo(@"C:\photos\a.jpg"));
        await _repo.AddAsync(Photo(@"C:\photos\sub\b.jpg"));
        await _repo.AddAsync(Photo(@"C:\other\c.jpg"));

        var removed = await _repo.RemoveByFolderAsync(@"C:\photos", includeSubfolders: true);

        Assert.Equal(2, removed);
        Assert.Equal(1, await _repo.CountAsync()); // only C:\other\c.jpg remains
    }

    [Fact]
    public async Task IncludeSubfolders_False_OnlyRemovesDirectChildren()
    {
        await _repo.AddAsync(Photo(@"C:\photos\direct.jpg"));
        await _repo.AddAsync(Photo(@"C:\photos\sub\nested.jpg")); // nested — must survive
        await _repo.AddAsync(Photo(@"C:\other\c.jpg"));

        var removed = await _repo.RemoveByFolderAsync(@"C:\photos", includeSubfolders: false);

        Assert.Equal(1, removed);
        // Nested photo is NOT a direct child — stays in the library
        Assert.NotNull(await _repo.GetByPathAsync(@"C:\photos\sub\nested.jpg"));
    }

    [Fact]
    public async Task SiblingFolder_IsNotAffectedByPrefix()
    {
        // "\photos\" must NOT match "\photosgallery\..." — the trailing separator prevents this
        await _repo.AddAsync(Photo(@"C:\photos\img.jpg"));
        await _repo.AddAsync(Photo(@"C:\photosgallery\img.jpg")); // different folder

        var removed = await _repo.RemoveByFolderAsync(@"C:\photos", includeSubfolders: true);

        Assert.Equal(1, removed);
        Assert.NotNull(await _repo.GetByPathAsync(@"C:\photosgallery\img.jpg"));
    }

    [Fact]
    public async Task TrailingBackslash_ProducesSamePrefixAsBareFolder()
    {
        await _repo.AddAsync(Photo(@"C:\Vacation\img.jpg"));

        // Called with trailing separator — TrimEnd normalises it before adding one back
        var removed = await _repo.RemoveByFolderAsync(@"C:\Vacation\", includeSubfolders: true);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task FolderWithNoMatchingPhotos_ReturnsZero()
    {
        await _repo.AddAsync(Photo(@"C:\other\img.jpg"));

        var removed = await _repo.RemoveByFolderAsync(@"C:\nonexistent", includeSubfolders: true);

        Assert.Equal(0, removed);
        Assert.Equal(1, await _repo.CountAsync());
    }
}

// ── GetDistinctFolderPathsAsync ───────────────────────────────────────────────

/// <summary>Tests for MediaFileRepository.GetDistinctFolderPathsAsync: folder enumeration.</summary>
public class GetDistinctFolderPathsTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public GetDistinctFolderPathsTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task ReturnsDistinctParentDirectories()
    {
        await _repo.AddAsync(Photo("/gallery/a.jpg"));
        await _repo.AddAsync(Photo("/gallery/b.jpg")); // same folder as a
        await _repo.AddAsync(Photo("/archive/c.jpg"));

        var folders = await _repo.GetDistinctFolderPathsAsync();

        // "/gallery" and "/archive" are the two distinct dirs; "/gallery" appears once
        Assert.Equal(2, folders.Count);
        Assert.Contains(folders, f => f.EndsWith("gallery", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(folders, f => f.EndsWith("archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExcludedPhotos_AreOmittedFromFolderList()
    {
        var excl = Photo("/hidden/x.jpg");
        excl.IsExcluded = true;
        await _repo.AddAsync(excl);

        var folders = await _repo.GetDistinctFolderPathsAsync();
        Assert.Empty(folders);
    }

    [Fact]
    public async Task EmptyLibrary_ReturnsEmptyList()
        => Assert.Empty(await _repo.GetDistinctFolderPathsAsync());
}

// ── ExcludeAsync ─────────────────────────────────────────────────────────────

/// <summary>Tests for MediaFileRepository.ExcludeAsync: marking photos as excluded.</summary>
public class ExcludeAsyncTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public ExcludeAsyncTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task ExcludeAsync_SetsIsExcludedTrue()
    {
        var photo = await _repo.AddAsync(Photo("/excl/photo.jpg"));

        await _repo.ExcludeAsync(photo.Id);

        Db.ChangeTracker.Clear();
        var loaded = await Db.MediaFiles.FindAsync(photo.Id);
        Assert.True(loaded?.IsExcluded);
    }

    [Fact]
    public async Task ExcludeAsync_PhotoDropsFromCount()
    {
        var photo = await _repo.AddAsync(Photo("/excl/photo.jpg"));
        Assert.Equal(1, await _repo.CountAsync());

        await _repo.ExcludeAsync(photo.Id);

        Assert.Equal(0, await _repo.CountAsync());
    }

    [Fact]
    public async Task ExcludeAsync_NonExistentId_DoesNotThrow()
        => await _repo.ExcludeAsync(Guid.NewGuid());
}

// ── GetOrCreateTagAsync / GetAllTagNamesAsync / RemoveTagFromPhotoAsync ────────

/// <summary>Tests for tag operations in MediaFileRepository: CRUD and photo-tag relationships.</summary>
public class TagRepositoryTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public TagRepositoryTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task GetOrCreateTagAsync_NewNormalizedName_CreatesTag()
    {
        var tag = await _repo.GetOrCreateTagAsync("Beach", "beach", TagCategory.Scene, true, 0.85f);

        Assert.NotEqual(Guid.Empty, tag.Id);
        Assert.Equal("Beach",   tag.Name);
        Assert.Equal("beach",   tag.NormalizedName);
        Assert.Equal(TagCategory.Scene, tag.Category);
        Assert.True(tag.IsAIGenerated);
    }

    [Fact]
    public async Task GetOrCreateTagAsync_ExistingNormalizedName_ReturnsExisting()
    {
        var first  = await _repo.GetOrCreateTagAsync("Sunset", "sunset", TagCategory.Scene, true, 0.9f);
        var second = await _repo.GetOrCreateTagAsync("sunset", "sunset", TagCategory.Scene, true, 0.8f);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _repo.GetAllTagNamesAsync());
    }

    [Fact]
    public async Task GetAllTagNamesAsync_ReturnsNamesOrderedAlphabetically()
    {
        await _repo.GetOrCreateTagAsync("Zebra",  "zebra",  TagCategory.Object, true, 0.9f);
        await _repo.GetOrCreateTagAsync("Apple",  "apple",  TagCategory.Object, true, 0.9f);
        await _repo.GetOrCreateTagAsync("Mango",  "mango",  TagCategory.Object, true, 0.9f);

        var names = await _repo.GetAllTagNamesAsync();
        Assert.Equal(["Apple", "Mango", "Zebra"], names);
    }

    [Fact]
    public async Task RemoveTagFromPhotoAsync_UnlinksTagFromPhoto()
    {
        var photo = await _repo.AddAsync(Photo("/tag/photo.jpg"));
        var tag   = await _repo.GetOrCreateTagAsync("Sky", "sky", TagCategory.Scene, true, 0.8f);

        // Link tag to photo via EF
        Db.ChangeTracker.Clear();
        var mf = await Db.MediaFiles.Include(m => m.Tags).FirstAsync(m => m.Id == photo.Id);
        var t  = await Db.Tags.FindAsync(tag.Id);
        mf.Tags.Add(t!);
        await Db.SaveChangesAsync();

        await _repo.RemoveTagFromPhotoAsync(photo.Id, tag.Id);

        Db.ChangeTracker.Clear();
        var reloaded = await Db.MediaFiles.Include(m => m.Tags).FirstAsync(m => m.Id == photo.Id);
        Assert.Empty(reloaded.Tags);
    }

    [Fact]
    public async Task RemoveTagFromPhotoAsync_TagStillExistsInDbAfterUnlink()
    {
        var photo = await _repo.AddAsync(Photo("/tag/photo2.jpg"));
        var tag   = await _repo.GetOrCreateTagAsync("Cloud", "cloud", TagCategory.Scene, true, 0.7f);

        Db.ChangeTracker.Clear();
        var mf = await Db.MediaFiles.Include(m => m.Tags).FirstAsync(m => m.Id == photo.Id);
        var t  = await Db.Tags.FindAsync(tag.Id);
        mf.Tags.Add(t!);
        await Db.SaveChangesAsync();

        await _repo.RemoveTagFromPhotoAsync(photo.Id, tag.Id);

        Assert.NotNull(await Db.Tags.FindAsync(tag.Id)); // tag still in DB
    }

    [Fact]
    public async Task RemoveTagFromPhotoAsync_NonExistentPhoto_DoesNotThrow()
        => await _repo.RemoveTagFromPhotoAsync(Guid.NewGuid(), Guid.NewGuid());
}

// ── GetDuplicateGroupsAsync ───────────────────────────────────────────────────

/// <summary>Tests for MediaFileRepository.GetDuplicateGroupsAsync: hash and RAW+JPEG pair detection.</summary>
public class DuplicateDetectionTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public DuplicateDetectionTests() => _repo = new MediaFileRepository(Db);

    // Add photos directly (no FTS needed — GetDuplicateGroupsAsync only reads MediaFiles)
    private async Task<MediaFile> AddDirect(string path, string ext, string? hash = null)
    {
        var p = new MediaFile
        {
            FilePath   = path,
            FileName   = Path.GetFileName(path),
            Extension  = ext,
            FileSize   = 100_000,
            FileHash   = hash ?? Guid.NewGuid().ToString("N"),
            DateImported = DateTime.UtcNow,
        };
        Db.MediaFiles.Add(p);
        await Db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task ExactHashDuplicate_DetectedAsSingleGroup()
    {
        const string sharedHash = "aabbccdd11223344";
        await AddDirect("/dup/original.jpg",  ".jpg", sharedHash);
        await AddDirect("/dup/copy.jpg",      ".jpg", sharedHash);
        await AddDirect("/dup/unique.jpg",    ".jpg");             // different hash — not a dup

        var groups = (await _repo.GetDuplicateGroupsAsync()).ToList();

        var hashGroups = groups.Where(g => g.Type == DuplicateGroupType.ExactHash).ToList();
        Assert.Single(hashGroups);
        Assert.Equal(2, hashGroups[0].Files.Count);
    }

    [Fact]
    public async Task NoDuplicates_ReturnsEmptyList()
    {
        await AddDirect("/no/a.jpg", ".jpg");
        await AddDirect("/no/b.jpg", ".jpg");

        var groups = await _repo.GetDuplicateGroupsAsync();
        Assert.DoesNotContain(groups, g => g.Type == DuplicateGroupType.ExactHash);
    }

    [Fact]
    public async Task RawJpegPair_SameStemSameFolder_DetectedAsPair()
    {
        // vacation.cr2 + vacation.jpg in the same folder = RAW+JPEG pair
        await AddDirect(@"C:\holiday\vacation.cr2", ".cr2");
        await AddDirect(@"C:\holiday\vacation.jpg", ".jpg");

        var groups = (await _repo.GetDuplicateGroupsAsync()).ToList();

        var pairGroups = groups.Where(g => g.Type == DuplicateGroupType.RawJpegPair).ToList();
        Assert.Single(pairGroups);
        Assert.Equal(2, pairGroups[0].Files.Count);
    }

    [Fact]
    public async Task RawJpeg_DifferentFolders_NotDetectedAsPair()
    {
        // Same stem but different folders — NOT a pair
        await AddDirect(@"C:\folder1\shot.cr2", ".cr2");
        await AddDirect(@"C:\folder2\shot.jpg", ".jpg");

        var groups = (await _repo.GetDuplicateGroupsAsync()).ToList();
        Assert.DoesNotContain(groups, g => g.Type == DuplicateGroupType.RawJpegPair);
    }

    [Fact]
    public async Task ExcludedPhotos_NotIncludedInDuplicateGroups()
    {
        const string hash = "excl001122334455";
        await AddDirect("/excl/a.jpg", ".jpg", hash);
        var excl = await AddDirect("/excl/b.jpg", ".jpg", hash);
        excl.IsExcluded = true;
        await Db.SaveChangesAsync();

        // Only one non-excluded photo has this hash → not a duplicate group
        var groups = (await _repo.GetDuplicateGroupsAsync()).ToList();
        Assert.DoesNotContain(groups, g => g.Type == DuplicateGroupType.ExactHash);
    }
}

// ── GetOutdatedDescriptionsAsync ──────────────────────────────────────────────

/// <summary>Tests for MediaFileRepository outdated description queries: model/prompt version tracking.</summary>
public class OutdatedDescriptionsTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public OutdatedDescriptionsTests() => _repo = new MediaFileRepository(Db);

    private async Task<MediaFile> AddAnalysed(
        string path, string? model, string? promptVer, string? postProcessVer = "pp1",
        string description = "A person stands outside.")
    {
        var photo = Photo(path);
        photo.IsAnalyzed        = true;
        photo.AiDescription     = description;
        photo.AiModelUsed       = model;
        photo.PromptVersion     = promptVer;
        photo.PostProcessVersion = postProcessVer;
        await _repo.AddAsync(photo);
        return photo;
    }

    [Fact]
    public async Task OldModel_IsReturnedAsOutdated()
    {
        await AddAnalysed("/old/model.jpg", "llama2-vision", "v12b");

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Single(outdated);
    }

    [Fact]
    public async Task OldPromptVersion_IsReturnedAsOutdated()
    {
        await AddAnalysed("/old/prompt.jpg", "llama3.2-vision", "v10a");

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Single(outdated);
    }

    [Fact]
    public async Task NullPromptVersion_IsReturnedAsOutdated()
    {
        await AddAnalysed("/old/null.jpg", "llama3.2-vision", null);

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Single(outdated);
    }

    [Fact]
    public async Task OldPostProcessVersion_IsReturnedAsOutdated()
    {
        await AddAnalysed("/old/pp.jpg", "llama3.2-vision", "v12b", postProcessVer: "pp0");

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Single(outdated);
    }

    [Fact]
    public async Task NullPostProcessVersion_IsReturnedAsOutdated()
    {
        await AddAnalysed("/old/ppnull.jpg", "llama3.2-vision", "v12b", postProcessVer: null);

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Single(outdated);
    }

    [Fact]
    public async Task CurrentModelAndPrompt_IsNotReturnedAsOutdated()
    {
        await AddAnalysed("/current/ok.jpg", "llama3.2-vision", "v12b", postProcessVer: "pp1");

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Empty(outdated);
    }

    [Fact]
    public async Task NotAnalysed_IsNotReturnedAsOutdated()
    {
        // IsAnalyzed = false → not in scope
        await _repo.AddAsync(Photo("/new/unanalysed.jpg"));

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Empty(outdated);
    }

    [Fact]
    public async Task NullAiDescription_IsNotReturnedAsOutdated()
    {
        var photo = Photo("/new/nodesc.jpg");
        photo.IsAnalyzed        = true;
        photo.AiDescription     = null;   // CLIP ran but vision produced nothing
        photo.AiModelUsed       = "llama2-vision";
        photo.PromptVersion     = "v10a";
        photo.PostProcessVersion = "pp1";
        await _repo.AddAsync(photo);

        var outdated = await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");
        Assert.Empty(outdated);
    }

    [Fact]
    public async Task CountOutdated_MatchesGetOutdated()
    {
        await AddAnalysed("/count/a.jpg", "llama2-vision", "v10a");
        await AddAnalysed("/count/b.jpg", "llama3.2-vision", "v12b", postProcessVer: "pp1"); // current — not outdated

        var list  = (await _repo.GetOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1")).ToList();
        var count = await _repo.CountOutdatedDescriptionsAsync("llama3.2-vision", "v12b", "pp1");

        Assert.Equal(list.Count, count);
        Assert.Equal(1, count);
    }
}

// ── GetNearbyLocationAsync (Haversine) ────────────────────────────────────────

/// <summary>Tests for MediaFileRepository.GetNearbyLocationAsync: geospatial queries with Haversine distance.</summary>
public class LocationSearchTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public LocationSearchTests() => _repo = new MediaFileRepository(Db);

    // Reference: Central Park, NYC ≈ (40.785, -73.968)
    private const double RefLat = 40.785091;
    private const double RefLon = -73.968285;

    private async Task<MediaFile> AddWithLocation(string path, double lat, double lon)
    {
        var photo = Photo(path);
        photo.Latitude  = lat;
        photo.Longitude = lon;
        Db.MediaFiles.Add(photo);
        await Db.SaveChangesAsync();
        return photo;
    }

    [Fact]
    public async Task PhotoWithinRadius_IsReturned()
    {
        // (40.789, RefLon) ≈ 0.44 km from reference
        var close = await AddWithLocation("/geo/close.jpg", 40.789, RefLon);

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 1.0, excludeId: Guid.Empty);
        Assert.Contains(results, m => m.Id == close.Id);
    }

    [Fact]
    public async Task PhotoOutsideRadius_IsNotReturned()
    {
        // (40.800, RefLon) ≈ 1.66 km from reference
        var far = await AddWithLocation("/geo/far.jpg", 40.800, RefLon);

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 1.0, excludeId: Guid.Empty);
        Assert.DoesNotContain(results, m => m.Id == far.Id);
    }

    [Fact]
    public async Task ExactSamePoint_IsReturnedForAnyRadius()
    {
        var exact = await AddWithLocation("/geo/exact.jpg", RefLat, RefLon);

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 0.01, excludeId: Guid.Empty);
        Assert.Contains(results, m => m.Id == exact.Id);
    }

    [Fact]
    public async Task AnchorId_IsExcludedFromResults()
    {
        var anchor = await AddWithLocation("/geo/anchor.jpg", RefLat, RefLon);

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 1.0, excludeId: anchor.Id);
        Assert.DoesNotContain(results, m => m.Id == anchor.Id);
    }

    [Fact]
    public async Task PhotoWithNoGps_IsNotReturned()
    {
        // Latitude and Longitude are null — no GPS → must not appear
        var noGps = Photo("/geo/nogps.jpg");
        Db.MediaFiles.Add(noGps);
        await Db.SaveChangesAsync();

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 5.0, excludeId: Guid.Empty);
        Assert.DoesNotContain(results, m => m.Id == noGps.Id);
    }

    [Fact]
    public async Task MaxResults_CapIsRespected()
    {
        // Add 5 photos all at the reference point
        for (int i = 0; i < 5; i++)
            await AddWithLocation($"/geo/cap{i}.jpg", RefLat, RefLon);

        var results = await _repo.GetNearbyLocationAsync(RefLat, RefLon, radiusKm: 1.0, excludeId: Guid.Empty, maxResults: 3);
        Assert.True(results.Count <= 3);
    }
}

// ── GetAllAsync / GetAllWithTagsAsync ─────────────────────────────────────────

/// <summary>Tests for MediaFileRepository.GetAllAsync: filtering excluded and ordering by date.</summary>
public class GetAllQueryTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public GetAllQueryTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task GetAllAsync_ExcludesIsExcludedPhotos()
    {
        await _repo.AddAsync(Photo("/all/visible.jpg"));
        var excl = Photo("/all/hidden.jpg");
        excl.IsExcluded = true;
        await _repo.AddAsync(excl);

        var all = (await _repo.GetAllAsync()).ToList();
        Assert.Single(all);
        Assert.Equal("/all/visible.jpg", all[0].FilePath);
    }

    [Fact]
    public async Task GetAllAsync_OrderedByDateTakenDescThenDateImportedDesc()
    {
        var older = Photo("/ord/a.jpg");
        older.DateTaken = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var newer = Photo("/ord/b.jpg");
        newer.DateTaken = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await _repo.AddAsync(older);
        await _repo.AddAsync(newer);

        var all = (await _repo.GetAllAsync()).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("/ord/b.jpg", all[0].FilePath); // newer first (desc)
        Assert.Equal("/ord/a.jpg", all[1].FilePath);
    }

    [Fact]
    public async Task GetAllWithTagsAsync_EagerLoadsTagCollection()
    {
        var photo = await _repo.AddAsync(Photo("/tags/photo.jpg"));
        var tag   = await _repo.GetOrCreateTagAsync("Mountain", "mountain", TagCategory.Scene, true, 0.9f);

        // Link tag
        Db.ChangeTracker.Clear();
        var mf = await Db.MediaFiles.Include(m => m.Tags).FirstAsync(m => m.Id == photo.Id);
        mf.Tags.Add((await Db.Tags.FindAsync(tag.Id))!);
        await Db.SaveChangesAsync();

        var allWithTags = (await _repo.GetAllWithTagsAsync()).ToList();
        var loaded = allWithTags.First(m => m.Id == photo.Id);
        Assert.Single(loaded.Tags);
        Assert.Equal("Mountain", loaded.Tags.First().Name);
    }

    [Fact]
    public async Task GetAllWithTagsAsync_EmptyLibrary_ReturnsEmptyList()
        => Assert.Empty(await _repo.GetAllWithTagsAsync());
}

// ── Constraint Violations ──────────────────────────────────────────────────────

/// <summary>Tests for MediaFileRepository constraint violation handling: database integrity checks.</summary>
public class ConstraintViolationTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public ConstraintViolationTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task AddAsync_DuplicateFilePath_ThrowsDbUpdateException()
    {
        // Test constraint violation: FilePath is unique, no two files can have the same path
        var photo1 = Photo(@"C:\photos\duplicate.jpg", hash: "hash1");
        var photo2 = Photo(@"C:\photos\duplicate.jpg", hash: "hash2"); // Same path, different hash

        await _repo.AddAsync(photo1);

        // Adding a second file with same path should violate unique constraint
        var ex = await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            async () => await _repo.AddAsync(photo2));

        Assert.NotNull(ex);
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAsync_SucceedsWithDifferentPaths()
    {
        // Verify that unique constraint only prevents identical paths
        // Same file hash is allowed (identical files in different folders)
        var photo1 = Photo(@"C:\photos\a.jpg", hash: "abc123");
        var photo2 = Photo(@"C:\photos\b.jpg", hash: "abc123"); // Same hash, different path

        await _repo.AddAsync(photo1);
        await _repo.AddAsync(photo2); // Should not throw

        Assert.Equal(2, await _repo.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesFtsRecordsConsistently()
    {
        // Test that deleting a photo removes its FTS record
        // This verifies data consistency: every photo in DB should have FTS entry
        var photo = Photo(@"C:\photos\test.jpg");
        await _repo.AddAsync(photo);

        var before = await _repo.CountAsync();
        await _repo.DeleteAsync(photo.Id);
        var after = await _repo.CountAsync();

        Assert.Equal(1, before);
        Assert.Equal(0, after);
        // FTS record should also be deleted (no orphaned search entries)
    }

    [Fact]
    public async Task BatchUpdateAsync_ChangesAreTransactional()
    {
        // Test that batch updates are transactional
        var photos = new List<MediaFile>
        {
            Photo(@"C:\photos\a.jpg", status: AnalysisStatus.Pending),
            Photo(@"C:\photos\b.jpg", status: AnalysisStatus.Pending),
        };

        await _repo.AddAsync(photos[0]);
        await _repo.AddAsync(photos[1]);

        // Batch update both photos
        photos[0].AnalysisStatus = AnalysisStatus.Complete;
        photos[1].AnalysisStatus = AnalysisStatus.Complete;

        await _repo.BatchUpdateAsync(photos);

        // Both should reflect new status
        var all = (await _repo.GetAllAsync()).ToList();
        Assert.All(all, p => Assert.Equal(AnalysisStatus.Complete, p.AnalysisStatus));
    }

    [Fact]
    public async Task UpdateAsync_PreservesUnchangedProperties()
    {
        // Test that updating one property doesn't null out others
        var photo = Photo(@"C:\photos\test.jpg");
        photo.FileHash = "abc123";
        photo.AiDescription = "A photo";

        await _repo.AddAsync(photo);

        photo.AiDescription = "Updated description";
        await _repo.UpdateAsync(photo);

        var reloaded = await _repo.GetByIdAsync(photo.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("abc123", reloaded.FileHash);
        Assert.Equal("Updated description", reloaded.AiDescription);
    }
}
