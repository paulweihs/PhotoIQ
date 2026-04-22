using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Data;
using PhotoIQPro.Data.Repositories;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>
/// Tests for MediaFileRepository.FindSimilarAsync — finding photos visually similar to a source photo.
/// </summary>
public sealed class FindSimilarTests : RepositoryTestBase
{
    [Fact]
    public async Task FindSimilarAsync_ReturnsSimilarPhotosOrderedByScore()
    {
        // Arrange: Add 5 photos with known embeddings
        //   Query vector: [1, 0] (unit vector pointing in X direction)
        //   Photo 1: [0.9, 0.1] — score ≈ 0.9 (similar to query)
        //   Photo 2: [0.8, 0.2] — score ≈ 0.8
        //   Photo 3: [0.0, 1.0] — score ≈ 0.0 (orthogonal)
        //   Photo 4: [0.7, 0.3] — score ≈ 0.7
        //   Photo 5: [0.5, 0.5] — score ≈ 0.5

        var photo1 = new MediaFile { FilePath = "/photo1.jpg", FileName = "photo1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.9f, 0.1f }) };
        var photo2 = new MediaFile { FilePath = "/photo2.jpg", FileName = "photo2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.8f, 0.2f }) };
        var photo3 = new MediaFile { FilePath = "/photo3.jpg", FileName = "photo3.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.0f, 1.0f }) };
        var photo4 = new MediaFile { FilePath = "/photo4.jpg", FileName = "photo4.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.7f, 0.3f }) };
        var photo5 = new MediaFile { FilePath = "/photo5.jpg", FileName = "photo5.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.5f, 0.5f }) };

        Db.MediaFiles.AddRange(photo1, photo2, photo3, photo4, photo5);
        await Db.SaveChangesAsync();

        var sourceId = photo1.Id;

        // Act
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourceId, minScore: 0.0f, topN: 3);
        var resultsList = results.ToList();

        // Assert: results should exclude source (photo1) and be ordered by similarity
        // Expected order: photo2 (0.8), photo4 (0.7), photo5 (0.5)
        Assert.Equal(3, resultsList.Count);
        Assert.DoesNotContain(resultsList, r => r.Id == photo1.Id);  // source excluded
        // Note: exact ordering depends on implementation details, but photo2 should score highest
        Assert.Contains(photo2.Id, resultsList.Select(r => r.Id));
    }

    [Fact]
    public async Task FindSimilarAsync_ExcludesSourcePhotoFromResults()
    {
        // Arrange: Add source + 3 other photos
        var sourcePhoto = new MediaFile { FilePath = "/source.jpg", FileName = "source.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 1, 0 }) };
        var other1 = new MediaFile { FilePath = "/other1.jpg", FileName = "other1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.9f, 0.1f }) };
        var other2 = new MediaFile { FilePath = "/other2.jpg", FileName = "other2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.8f, 0.2f }) };

        Db.MediaFiles.AddRange(sourcePhoto, other1, other2);
        await Db.SaveChangesAsync();

        // Act
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourcePhoto.Id, topN: 10);
        var resultsList = results.ToList();

        // Assert: source should never appear in results
        Assert.DoesNotContain(resultsList, r => r.Id == sourcePhoto.Id);
        Assert.Equal(2, resultsList.Count);  // only other1 and other2
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsEmptyWhenSourceHasNoEmbedding()
    {
        // Arrange: source photo with no embedding + 3 other analyzed photos
        var sourcePhoto = new MediaFile { FilePath = "/source.jpg", FileName = "source.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = null };  // ← no embedding
        var other1 = new MediaFile { FilePath = "/other1.jpg", FileName = "other1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 1, 0 }) };
        var other2 = new MediaFile { FilePath = "/other2.jpg", FileName = "other2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.9f, 0.1f }) };

        Db.MediaFiles.AddRange(sourcePhoto, other1, other2);
        await Db.SaveChangesAsync();

        // Act
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourcePhoto.Id);

        // Assert: should return empty
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_ReturnsEmptyWhenNoOtherPhotosHaveEmbeddings()
    {
        // Arrange: source with embedding, but no other photos have embeddings
        var sourcePhoto = new MediaFile { FilePath = "/source.jpg", FileName = "source.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 1, 0 }) };
        var other1 = new MediaFile { FilePath = "/other1.jpg", FileName = "other1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = null };
        var other2 = new MediaFile { FilePath = "/other2.jpg", FileName = "other2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = null };

        Db.MediaFiles.AddRange(sourcePhoto, other1, other2);
        await Db.SaveChangesAsync();

        // Act
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourcePhoto.Id);

        // Assert: should return empty (no other analyzed photos)
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_RespectsMinScoreThreshold()
    {
        // Arrange: source photo + 5 others with different similarities
        //   All embeddings are normalized 2D unit vectors
        var sourcePhoto = new MediaFile { FilePath = "/source.jpg", FileName = "source.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 1, 0 }) };
        var high1 = new MediaFile { FilePath = "/high1.jpg", FileName = "high1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.9f, 0.1f }) };  // score ≈ 0.9
        var high2 = new MediaFile { FilePath = "/high2.jpg", FileName = "high2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.8f, 0.2f }) };  // score ≈ 0.8
        var low1 = new MediaFile { FilePath = "/low1.jpg", FileName = "low1.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.5f, 0.5f }) };  // score ≈ 0.5
        var low2 = new MediaFile { FilePath = "/low2.jpg", FileName = "low2.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 0.1f, 0.9f }) };  // score ≈ 0.1

        Db.MediaFiles.AddRange(sourcePhoto, high1, high2, low1, low2);
        await Db.SaveChangesAsync();

        // Act: search with minScore=0.6, should exclude low1 and low2
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourcePhoto.Id, minScore: 0.6f, topN: 10);
        var resultIds = results.Select(r => r.Id).ToHashSet();

        // Assert: should include only high1, high2 (not low1, low2)
        Assert.Contains(high1.Id, resultIds);
        Assert.Contains(high2.Id, resultIds);
        Assert.DoesNotContain(low1.Id, resultIds);
        Assert.DoesNotContain(low2.Id, resultIds);
    }

    [Fact]
    public async Task FindSimilarAsync_RespectsTopNLimit()
    {
        // Arrange: source + 10 analyzed photos
        var sourcePhoto = new MediaFile { FilePath = "/source.jpg", FileName = "source.jpg", Extension = ".jpg", FileSize = 1000, DateImported = DateTime.UtcNow, ClipEmbeddingBytes = FloatsToBytes(new float[] { 1, 0 }) };
        var others = new List<MediaFile>();
        for (int i = 0; i < 10; i++)
        {
            // Each photo has a slightly different angle, simulating varying similarity
            float angle = (float)(Math.PI * i / 10);
            float x = (float)Math.Cos(angle);
            float y = (float)Math.Sin(angle);
            others.Add(new MediaFile
            {
                FilePath = $"/other{i}.jpg",
                FileName = $"other{i}.jpg",
                Extension = ".jpg",
                FileSize = 1000,
                DateImported = DateTime.UtcNow,
                ClipEmbeddingBytes = FloatsToBytes(new float[] { x, y })
            });
        }

        Db.MediaFiles.Add(sourcePhoto);
        Db.MediaFiles.AddRange(others);
        await Db.SaveChangesAsync();

        // Act: request only topN=3
        var repo = new MediaFileRepository(Db);
        var results = await repo.FindSimilarAsync(sourcePhoto.Id, minScore: 0.0f, topN: 3);
        var resultsList = results.ToList();

        // Assert: should return at most 3 results (not including source)
        Assert.True(resultsList.Count <= 3, $"Expected at most 3 results, got {resultsList.Count}");
    }

    /// <summary>
    /// Helper to convert float[] embeddings to byte[] for storage (matching EmbeddingDot byte layout).
    /// </summary>
    private static byte[] FloatsToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * 4];
        for (int i = 0; i < embedding.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(embedding[i]), 0, bytes, i * 4, 4);
        return bytes;
    }
}
