using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Data;
using PhotoIQPro.Data.Repositories;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>
/// Integration tests for PersonRepository and the face-related MediaFileRepository methods.
/// Covers the N+1 fix in GetAllSummariesAsync and the null-key fix in GetTagPhotoCountsAsync.
/// </summary>
public class PersonRepositoryTests : RepositoryTestBase
{
    private readonly PersonRepository      _people;
    private readonly MediaFileRepository   _media;

    public PersonRepositoryTests()
    {
        _people = new PersonRepository(Db);
        _media  = new MediaFileRepository(Db);

        // People and Faces tables are created by EF Core model (PhotoIQContext).
        // EnsureCreated() in the base class handles this.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Person MakePerson(string? name = null, bool isNamed = false)
        => new()
        {
            Name           = name,
            NormalizedName = name?.ToLowerInvariant(),
            IsNamed        = isNamed,
        };

    private static Face MakeFace(Guid mediaFileId, Guid? personId = null)
        => new()
        {
            MediaFileId = mediaFileId,
            PersonId    = personId,
            X           = 0.1,
            Y           = 0.1,
            Width       = 0.2,
            Height      = 0.2,
        };

    // ── CreateAsync / GetByIdAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_PersistsAndReturnsWithId()
    {
        var person = await _people.CreateAsync(MakePerson("Alice", true));

        Assert.NotEqual(Guid.Empty, person.Id);
        var fetched = await _people.GetByIdAsync(person.Id);
        Assert.Equal("Alice", fetched?.Name);
    }

    // ── FindOrCreateByNameAsync ────────────────────────────────────────────────

    [Fact]
    public async Task FindOrCreateByNameAsync_CreatesNewPerson()
    {
        var person = await _people.FindOrCreateByNameAsync("Bob", "bob");

        Assert.Equal("Bob", person.Name);
        Assert.True(person.IsNamed);
    }

    [Fact]
    public async Task FindOrCreateByNameAsync_ReturnsExistingOnSecondCall()
    {
        var first  = await _people.FindOrCreateByNameAsync("Carol", "carol");
        var second = await _people.FindOrCreateByNameAsync("Carol", "carol");

        Assert.Equal(first.Id, second.Id);
    }

    // ── GetAllSummariesAsync — was N+1, now 3 queries ─────────────────────────

    [Fact]
    public async Task GetAllSummariesAsync_ReturnsCorrectPhotoCounts()
    {
        var photo1 = Photo(@"C:\a.jpg");
        var photo2 = Photo(@"C:\b.jpg");
        await _media.AddAsync(photo1);
        await _media.AddAsync(photo2);

        var person = await _people.CreateAsync(MakePerson("Dave", true));

        // Two faces for dave, both in different photos.
        var face1 = MakeFace(photo1.Id, person.Id);
        var face2 = MakeFace(photo2.Id, person.Id);
        Db.Faces.AddRange(face1, face2);
        await Db.SaveChangesAsync();

        var summaries = await _people.GetAllSummariesAsync();

        Assert.Single(summaries);
        Assert.Equal(2, summaries[0].PhotoCount);
    }

    [Fact]
    public async Task GetAllSummariesAsync_EmptyDb_ReturnsEmpty()
    {
        var summaries = await _people.GetAllSummariesAsync();
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetAllSummariesAsync_NamedPersonsFirst()
    {
        await _people.CreateAsync(MakePerson(isNamed: false));  // unnamed
        await _people.CreateAsync(MakePerson("Eve", isNamed: true)); // named

        var summaries = await _people.GetAllSummariesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.True(summaries[0].Person.IsNamed, "Named person should come first");
        Assert.False(summaries[1].Person.IsNamed);
    }

    [Fact]
    public async Task GetAllSummariesAsync_HiddenPersonsExcluded()
    {
        var visible = await _people.CreateAsync(MakePerson("Visible", true));
        var hidden  = await _people.CreateAsync(MakePerson("Hidden",  true));
        await _people.HideAsync(hidden.Id);

        var summaries = await _people.GetAllSummariesAsync();

        Assert.Single(summaries);
        Assert.Equal(visible.Id, summaries[0].Person.Id);
    }

    // ── MergeIntoAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task MergeIntoAsync_RelinksAllFacesToTarget()
    {
        var photo = Photo(@"C:\c.jpg");
        await _media.AddAsync(photo);

        var source = await _people.CreateAsync(MakePerson("Source", true));
        var target = await _people.CreateAsync(MakePerson("Target", true));

        var face = MakeFace(photo.Id, source.Id);
        Db.Faces.Add(face);
        await Db.SaveChangesAsync();

        await _people.MergeIntoAsync(source.Id, target.Id);

        var relinkedFace = await Db.Faces.FirstAsync(f => f.Id == face.Id);
        Assert.Equal(target.Id, relinkedFace.PersonId);

        var deletedSource = await _people.GetByIdAsync(source.Id);
        Assert.Null(deletedSource);
    }

    // ── GetAllEmbeddingsAsync — was ContinueWith, now proper await ────────────

    [Fact]
    public async Task GetAllEmbeddingsAsync_ReturnsOnlyPeopleWithEmbeddings()
    {
        var withEmb    = await _people.CreateAsync(MakePerson("WithEmb", true));
        var withoutEmb = await _people.CreateAsync(MakePerson("NoEmb",   true));

        withEmb.AverageEmbedding = new byte[128 * sizeof(float)];
        await _people.UpdateAsync(withEmb);

        var embeddings = await _people.GetAllEmbeddingsAsync();

        Assert.Single(embeddings);
        Assert.Equal(withEmb.Id, embeddings[0].PersonId);
    }
}

/// <summary>
/// Tests for GetTagPhotoCountsAsync — specifically the null-key bug fix.
/// </summary>
public class TagPhotoCountsTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    public TagPhotoCountsTests() => _repo = new MediaFileRepository(Db);

    [Fact]
    public async Task GetTagPhotoCountsAsync_ReturnsCountsPerTag()
    {
        var photo = Photo(@"C:\tagged.jpg");
        await _repo.AddAsync(photo);

        var tag = new Tag { Name = "sunset", NormalizedName = "sunset", IsAIGenerated = true };
        Db.Tags.Add(tag);
        var mf = await Db.MediaFiles.FindAsync(photo.Id);
        mf!.Tags.Add(tag);
        await Db.SaveChangesAsync();

        var counts = await _repo.GetTagPhotoCountsAsync();

        Assert.True(counts.ContainsKey("sunset"));
        Assert.Equal(1, counts["sunset"]);
    }

    [Fact]
    public async Task GetTagPhotoCountsAsync_ExcludedPhotosNotCounted()
    {
        var photo = Photo(@"C:\excluded.jpg");
        await _repo.AddAsync(photo);
        var mf = await Db.MediaFiles.FindAsync(photo.Id);
        mf!.IsExcluded = true;

        var tag = new Tag { Name = "landscape", NormalizedName = "landscape", IsAIGenerated = true };
        Db.Tags.Add(tag);
        mf.Tags.Add(tag);
        await Db.SaveChangesAsync();

        var counts = await _repo.GetTagPhotoCountsAsync();

        // Tag exists but excluded photo doesn't count.
        Assert.True(counts.ContainsKey("landscape"));
        Assert.Equal(0, counts["landscape"]);
    }

    [Fact]
    public async Task GetTagPhotoCountsAsync_EmptyDb_ReturnsEmpty()
    {
        var counts = await _repo.GetTagPhotoCountsAsync();
        Assert.Empty(counts);
    }

    [Fact]
    public async Task GetTagPhotoCountsAsync_TagSharedByMultiplePhotos_CountsAll()
    {
        var p1 = Photo(@"C:\p1.jpg");
        var p2 = Photo(@"C:\p2.jpg");
        await _repo.AddAsync(p1);
        await _repo.AddAsync(p2);

        var tag = new Tag { Name = "travel", NormalizedName = "travel", IsAIGenerated = true };
        Db.Tags.Add(tag);
        var mf1 = await Db.MediaFiles.FindAsync(p1.Id);
        var mf2 = await Db.MediaFiles.FindAsync(p2.Id);
        mf1!.Tags.Add(tag);
        mf2!.Tags.Add(tag);
        await Db.SaveChangesAsync();

        var counts = await _repo.GetTagPhotoCountsAsync();

        Assert.Equal(2, counts["travel"]);
    }
}

/// <summary>
/// Tests for GetOnThisDayAsync and face-query methods on MediaFileRepository.
/// </summary>
public class OnThisDayAndFaceQueryTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    private readonly PersonRepository    _people;

    public OnThisDayAndFaceQueryTests()
    {
        _repo   = new MediaFileRepository(Db);
        _people = new PersonRepository(Db);
    }

    // ── GetOnThisDayAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetOnThisDayAsync_ReturnsPhotosWithSameMonthAndDay()
    {
        var today = new DateTime(2026, 4, 14);
        var match = Photo(@"C:\m.jpg"); match.DateTaken = new DateTime(2023, 4, 14, 10, 0, 0);
        var noMatch = Photo(@"C:\n.jpg"); noMatch.DateTaken = new DateTime(2023, 4, 15, 10, 0, 0);
        await _repo.AddAsync(match);
        await _repo.AddAsync(noMatch);

        var results = await _repo.GetOnThisDayAsync(today);

        Assert.Single(results);
        Assert.Equal(match.Id, results[0].Id);
    }

    [Fact]
    public async Task GetOnThisDayAsync_ExcludesCurrentYear()
    {
        var today = new DateTime(2026, 4, 14);
        var thisYear = Photo(@"C:\ty.jpg"); thisYear.DateTaken = new DateTime(2026, 4, 14);
        var lastYear = Photo(@"C:\ly.jpg"); lastYear.DateTaken = new DateTime(2025, 4, 14);
        await _repo.AddAsync(thisYear);
        await _repo.AddAsync(lastYear);

        var results = await _repo.GetOnThisDayAsync(today);

        Assert.Single(results);
        Assert.Equal(lastYear.Id, results[0].Id);
    }

    [Fact]
    public async Task GetOnThisDayAsync_ExcludesExcludedPhotos()
    {
        var today = new DateTime(2026, 6, 1);
        var excluded = Photo(@"C:\ex.jpg"); excluded.DateTaken = new DateTime(2024, 6, 1); excluded.IsExcluded = true;
        var visible  = Photo(@"C:\vi.jpg"); visible.DateTaken  = new DateTime(2024, 6, 1);
        await _repo.AddAsync(excluded);
        await _repo.AddAsync(visible);

        var results = await _repo.GetOnThisDayAsync(today);

        Assert.Single(results);
        Assert.Equal(visible.Id, results[0].Id);
    }

    [Fact]
    public async Task GetOnThisDayAsync_PhotosWithNullDateTaken_NotReturned()
    {
        var today = new DateTime(2026, 4, 14);
        var noDate = Photo(@"C:\nd.jpg");  // DateTaken = null
        await _repo.AddAsync(noDate);

        var results = await _repo.GetOnThisDayAsync(today);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetOnThisDayAsync_OrderedByYearDescThenTimeAsc()
    {
        var today = new DateTime(2026, 4, 14);
        var p2023am = Photo(@"C:\a.jpg"); p2023am.DateTaken = new DateTime(2023, 4, 14, 8, 0, 0);
        var p2023pm = Photo(@"C:\b.jpg"); p2023pm.DateTaken = new DateTime(2023, 4, 14, 18, 0, 0);
        var p2022   = Photo(@"C:\c.jpg"); p2022.DateTaken   = new DateTime(2022, 4, 14, 12, 0, 0);
        await _repo.AddAsync(p2023am);
        await _repo.AddAsync(p2022);
        await _repo.AddAsync(p2023pm);

        var results = await _repo.GetOnThisDayAsync(today);

        Assert.Equal(3, results.Count);
        Assert.Equal(2023, results[0].DateTaken!.Value.Year);  // 2023 first
        Assert.Equal(2023, results[1].DateTaken!.Value.Year);
        Assert.True(results[0].DateTaken!.Value.TimeOfDay < results[1].DateTaken!.Value.TimeOfDay); // AM before PM
        Assert.Equal(2022, results[2].DateTaken!.Value.Year);  // 2022 last
    }

    // ── GetFacesForPhotoAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetFacesForPhotoAsync_ReturnsFacesForPhoto()
    {
        var photo = Photo(@"C:\f.jpg");
        await _repo.AddAsync(photo);
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 });
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.5, Y = 0.1, Width = 0.2, Height = 0.2 });
        await Db.SaveChangesAsync();

        var faces = await _repo.GetFacesForPhotoAsync(photo.Id);

        Assert.Equal(2, faces.Count);
    }

    [Fact]
    public async Task GetFacesForPhotoAsync_DoesNotReturnFacesFromOtherPhotos()
    {
        var p1 = Photo(@"C:\p1.jpg");
        var p2 = Photo(@"C:\p2.jpg");
        await _repo.AddAsync(p1);
        await _repo.AddAsync(p2);
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = p1.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 });
        await Db.SaveChangesAsync();

        var faces = await _repo.GetFacesForPhotoAsync(p2.Id);

        Assert.Empty(faces);
    }

    // ── GetFacePhotoIdsForPersonAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetFacePhotoIdsForPersonAsync_ReturnsDistinctPhotoIds()
    {
        var photo1 = Photo(@"C:\pp1.jpg");
        var photo2 = Photo(@"C:\pp2.jpg");
        await _repo.AddAsync(photo1);
        await _repo.AddAsync(photo2);

        // Person must exist (FK constraint) — use the repository to properly persist.
        var person = await _people.FindOrCreateByNameAsync("Test", "test");

        // Two faces for the same person in photo1 — should deduplicate.
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = photo1.Id, PersonId = person.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 });
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = photo1.Id, PersonId = person.Id, X = 0.6, Y = 0.1, Width = 0.2, Height = 0.2 });
        Db.Faces.Add(new PhotoIQPro.Core.Models.Face { MediaFileId = photo2.Id, PersonId = person.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 });
        await Db.SaveChangesAsync();

        var photoIds = await _repo.GetFacePhotoIdsForPersonAsync(person.Id);

        Assert.Equal(2, photoIds.Count);
        Assert.Contains(photo1.Id, photoIds);
        Assert.Contains(photo2.Id, photoIds);
    }

    // ── MarkFacesReviewedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task MarkFacesReviewedAsync_SetsFacesReviewedTrue()
    {
        var photo = Photo(@"C:\rev.jpg");
        await _repo.AddAsync(photo);
        Assert.False(photo.FacesReviewed);

        await _repo.MarkFacesReviewedAsync(photo.Id);

        var fetched = await _repo.GetByIdAsync(photo.Id);
        Assert.True(fetched?.FacesReviewed);
    }

    [Fact]
    public async Task MarkFacesReviewedAsync_NonExistentId_DoesNotThrow()
    {
        await _repo.MarkFacesReviewedAsync(Guid.NewGuid()); // should not throw
    }
}

/// <summary>
/// Tests for new batch operations: BatchAddFacesAsync, BatchLinkFacesToPersonAsync, GetFacesByIdsAsync.
/// These ensure the optimizations work correctly without N+1 query patterns.
/// </summary>
public class FaceBatchOperationsTests : RepositoryTestBase
{
    private readonly MediaFileRepository _repo;
    private readonly PersonRepository    _people;

    public FaceBatchOperationsTests()
    {
        _repo   = new MediaFileRepository(Db);
        _people = new PersonRepository(Db);
    }

    // ── BatchAddFacesAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task BatchAddFacesAsync_PersistsMultipleFaces()
    {
        var photo = Photo(@"C:\batch.jpg");
        await _repo.AddAsync(photo);

        var faces = new[]
        {
            new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 },
            new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.5, Y = 0.1, Width = 0.2, Height = 0.2 },
            new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.7, Y = 0.5, Width = 0.15, Height = 0.15 },
        };

        await _repo.BatchAddFacesAsync(faces);

        var fetched = await _repo.GetFacesForPhotoAsync(photo.Id);
        Assert.Equal(3, fetched.Count);
    }

    [Fact]
    public async Task BatchAddFacesAsync_EmptyList_DoesNotThrow()
    {
        await _repo.BatchAddFacesAsync(Array.Empty<PhotoIQPro.Core.Models.Face>());
        // Should not throw
    }

    [Fact]
    public async Task BatchAddFacesAsync_SingleFace_Works()
    {
        var photo = Photo(@"C:\single.jpg");
        await _repo.AddAsync(photo);

        var face = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.3, Y = 0.3, Width = 0.2, Height = 0.2 };
        await _repo.BatchAddFacesAsync(new[] { face });

        var fetched = await _repo.GetFacesForPhotoAsync(photo.Id);
        Assert.Single(fetched);
    }

    // ── GetFacesByIdsAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFacesByIdsAsync_RetrievesMultipleFaces()
    {
        var photo = Photo(@"C:\getbyid.jpg");
        await _repo.AddAsync(photo);

        var f1 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 };
        var f2 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.5, Y = 0.1, Width = 0.2, Height = 0.2 };
        Db.Faces.AddRange(f1, f2);
        await Db.SaveChangesAsync();

        var retrieved = await _repo.GetFacesByIdsAsync(new[] { f1.Id, f2.Id });

        Assert.Equal(2, retrieved.Count);
        Assert.Contains(retrieved, f => f.Id == f1.Id);
        Assert.Contains(retrieved, f => f.Id == f2.Id);
    }

    [Fact]
    public async Task GetFacesByIdsAsync_EmptyList_ReturnsEmpty()
    {
        var retrieved = await _repo.GetFacesByIdsAsync(Array.Empty<Guid>());
        Assert.Empty(retrieved);
    }

    [Fact]
    public async Task GetFacesByIdsAsync_PartialMismatch_ReturnsOnlyFound()
    {
        var photo = Photo(@"C:\partial.jpg");
        await _repo.AddAsync(photo);

        var f1 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 };
        Db.Faces.Add(f1);
        await Db.SaveChangesAsync();

        var nonexistentId = Guid.NewGuid();
        var retrieved = await _repo.GetFacesByIdsAsync(new[] { f1.Id, nonexistentId });

        Assert.Single(retrieved);
        Assert.Equal(f1.Id, retrieved[0].Id);
    }

    // ── BatchLinkFacesToPersonAsync ────────────────────────────────────────────────

    [Fact]
    public async Task BatchLinkFacesToPersonAsync_LinksMultipleFaces()
    {
        var photo = Photo(@"C:\batchlink.jpg");
        await _repo.AddAsync(photo);

        var person = await _people.FindOrCreateByNameAsync("BatchTest", "batchtest");

        var f1 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 };
        var f2 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.5, Y = 0.1, Width = 0.2, Height = 0.2 };
        Db.Faces.AddRange(f1, f2);
        await Db.SaveChangesAsync();

        // Verify faces exist before linking
        var before1 = await Db.Faces.FirstOrDefaultAsync(f => f.Id == f1.Id);
        var before2 = await Db.Faces.FirstOrDefaultAsync(f => f.Id == f2.Id);
        Assert.NotNull(before1);
        Assert.NotNull(before2);
        Assert.Null(before1.PersonId);
        Assert.Null(before2.PersonId);

        // Link both faces to the person in a single batch operation
        await _people.BatchLinkFacesToPersonAsync(new[] { f1.Id, f2.Id }, person.Id, 0.95);

        // ExecuteUpdateAsync doesn't refresh in-memory entities; must reload from DB
        Db.ChangeTracker.Clear();
        var linked1 = await Db.Faces.FirstOrDefaultAsync(f => f.Id == f1.Id);
        var linked2 = await Db.Faces.FirstOrDefaultAsync(f => f.Id == f2.Id);

        Assert.NotNull(linked1);
        Assert.NotNull(linked2);
        Assert.Equal(person.Id, linked1.PersonId);
        Assert.Equal(person.Id, linked2.PersonId);
        Assert.Equal(0.95, linked1.IdentificationConfidence);
        Assert.Equal(0.95, linked2.IdentificationConfidence);
    }

    [Fact]
    public async Task BatchLinkFacesToPersonAsync_EmptyList_DoesNotThrow()
    {
        var person = await _people.FindOrCreateByNameAsync("Empty", "empty");
        await _people.BatchLinkFacesToPersonAsync(Array.Empty<Guid>(), person.Id, 0.9);
        // Should not throw
    }

    [Fact]
    public async Task BatchLinkFacesToPersonAsync_PartialMismatch_LinkOnlyFound()
    {
        var photo = Photo(@"C:\partialbatch.jpg");
        await _repo.AddAsync(photo);

        var person = await _people.FindOrCreateByNameAsync("Partial", "partial");

        var f1 = new PhotoIQPro.Core.Models.Face { MediaFileId = photo.Id, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 };
        Db.Faces.Add(f1);
        await Db.SaveChangesAsync();

        var nonexistentId = Guid.NewGuid();
        await _people.BatchLinkFacesToPersonAsync(new[] { f1.Id, nonexistentId }, person.Id, 0.85);

        // ExecuteUpdateAsync doesn't refresh in-memory entities; must reload from DB
        Db.ChangeTracker.Clear();
        var linked = await Db.Faces.FindAsync(f1.Id);
        Assert.Equal(person.Id, linked!.PersonId);
        Assert.Equal(0.85, linked.IdentificationConfidence);
    }
}
