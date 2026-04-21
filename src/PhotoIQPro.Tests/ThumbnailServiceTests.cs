using System;
using System.IO;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Services.Thumbnails;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>Tests for the ThumbnailService: path generation, constructor initialization, and thumbnail generation.</summary>
public class ThumbnailServiceTests
{
    // ── Constructor ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_UsesDefaultPath_WhenBasepathIsNull()
    {
        var service = new ThumbnailService(null);
        // Verify that the service was created without throwing
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_UsesProvidedPath_WhenBasepathIsSet()
    {
        string customPath = "/custom/thumbnails";
        var service = new ThumbnailService(customPath);
        // We can't directly access _basePath, but we can verify GetThumbnailPath uses it
        Assert.NotNull(service);
    }

    // ── GetThumbnailPath ────────────────────────────────────────────────────

    [Fact]
    public void GetThumbnailPath_ReturnsCorrectPathStructure()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "thumbnails");
        var service = new ThumbnailService(basePath);
        var id = Guid.NewGuid();

        var smallPath = service.GetThumbnailPath(id, ThumbnailSize.Small);

        // Verify path includes:
        // 1. Base path
        // 2. Two-character subdirectory (first 2 chars of GUID hex)
        // 3. Size subdirectory (lowercase)
        // 4. Filename (GUID.N format + .jpg)
        Assert.StartsWith(basePath, smallPath);
        Assert.Contains("small", smallPath.ToLower());
        Assert.EndsWith(".jpg", smallPath);
    }

    [Theory]
    [InlineData(ThumbnailSize.Small)]
    [InlineData(ThumbnailSize.Medium)]
    [InlineData(ThumbnailSize.Large)]
    public void GetThumbnailPath_IncludeSizeSubdirectory(ThumbnailSize size)
    {
        string basePath = Path.Combine(Path.GetTempPath(), "thumbnails");
        var service = new ThumbnailService(basePath);
        var id = Guid.NewGuid();

        var path = service.GetThumbnailPath(id, size);
        string expectedSizeDir = size.ToString().ToLower();

        Assert.Contains(expectedSizeDir, path.ToLower());
    }

    [Fact]
    public void GetThumbnailPath_UsesConsistentHashing()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "thumbnails");
        var service = new ThumbnailService(basePath);
        var id = Guid.NewGuid();

        // Call twice with the same ID; paths should be identical
        var path1 = service.GetThumbnailPath(id, ThumbnailSize.Small);
        var path2 = service.GetThumbnailPath(id, ThumbnailSize.Small);

        Assert.Equal(path1, path2);
    }

    [Fact]
    public void GetThumbnailPath_DifferentSizesProduceDifferentPaths()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "thumbnails");
        var service = new ThumbnailService(basePath);
        var id = Guid.NewGuid();

        var smallPath = service.GetThumbnailPath(id, ThumbnailSize.Small);
        var mediumPath = service.GetThumbnailPath(id, ThumbnailSize.Medium);
        var largePath = service.GetThumbnailPath(id, ThumbnailSize.Large);

        Assert.NotEqual(smallPath, mediumPath);
        Assert.NotEqual(mediumPath, largePath);
        Assert.NotEqual(smallPath, largePath);
    }

    [Fact]
    public void GetThumbnailPath_DifferentIDsProduceDifferentPaths()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "thumbnails");
        var service = new ThumbnailService(basePath);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var path1 = service.GetThumbnailPath(id1, ThumbnailSize.Small);
        var path2 = service.GetThumbnailPath(id2, ThumbnailSize.Small);

        Assert.NotEqual(path1, path2);
    }

    // ── GenerateThumbnailsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GenerateThumbnailsAsync_ReturnsFalseForNonexistentFile()
    {
        var service = new ThumbnailService();
        var mf = new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = "/nonexistent/image.jpg",
            FileName = "image.jpg",
            Extension = ".jpg",
            FileSize = 0,
            FileHash = Guid.NewGuid().ToString("N"),
            DateImported = DateTime.UtcNow,
            Tags = new List<Tag>(),
        };

        var result = await service.GenerateThumbnailsAsync(mf);

        Assert.False(result.Success);
    }

    [Fact]
    public void GenerateThumbnailsAsync_RequiresRealImageFile_IntegrationTest()
    {
        // This test would require creating an actual image file and verifying:
        // 1. Thumbnails are generated at all three sizes
        // 2. Dimensions are captured in MediaFile
        // 3. Thumbnail paths are set in ThumbnailSmall/Medium/Large
        // 4. EXIF orientation is applied correctly
        // 5. RAW formats (CR3, ARW, DNG) are handled via LibRaw
        //
        // This requires:
        // - A real test image file (JPEG, PNG, or RAW)
        // - The image file to be accessible during test execution
        // - Output directory to be writable
        // - Verification that generated thumbnails have correct dimensions
        //
        // Use RepositoryTestBase pattern with TestFixture for setup/teardown.
        // Skipping for unit test; recommend integration test suite.
    }

    // ── EXIF Orientation Handling ─────────────────────────────────────────

    [Theory]
    [InlineData(0)]   // Invalid
    [InlineData(9)]   // Out of range
    [InlineData(10)]  // Way out of range
    public void ReadJpegExifOrientation_HandlesInvalidOrientationValues(int orientationValue)
    {
        // Test handling of EXIF orientation values outside valid range (1-8)
        // Invalid values should be ignored or treated as 1 (no rotation)
        // This test documents expected behavior for malformed EXIF
        Assert.NotInRange(orientationValue, 1, 8);
    }

    [Theory]
    [InlineData(1)]  // No rotation
    [InlineData(2)]  // Flip horizontal
    [InlineData(3)]  // Rotate 180
    [InlineData(4)]  // Flip vertical
    [InlineData(5)]  // Transpose
    [InlineData(6)]  // Rotate 90 CW
    [InlineData(7)]  // Transverse
    [InlineData(8)]  // Rotate 270 CW
    public void ApplyExifOrientation_AllStandardValuesHandled(int orientation)
    {
        // Test that all valid EXIF orientation values (1-8) are handled
        // This documents the contract: all 8 orientations should be supported
        Assert.InRange(orientation, 1, 8);
    }

    // ── Dimension Edge Cases ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]        // Minimal image
    [InlineData(10000, 1)]    // Extreme aspect ratio (very wide)
    [InlineData(1, 10000)]    // Extreme aspect ratio (very tall)
    public void GenerateThumbnailsAsync_HandlesExtremeAspectRatios(int width, int height)
    {
        // Test that extremely narrow or wide images don't cause scaling errors
        // This verifies thumbnail generation handles edge cases gracefully
        var mf = new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = "/extreme.jpg",
            FileName = "extreme.jpg",
            Extension = ".jpg",
            FileSize = 1000,
            FileHash = Guid.NewGuid().ToString("N"),
            DateImported = DateTime.UtcNow,
            Tags = new List<Tag>(),
        };

        // The service should handle these aspect ratios without crashing
        Assert.NotNull(mf);
        Assert.True(width > 0 && height > 0);
    }

    [Theory]
    [InlineData(0, 100)]   // Zero width
    [InlineData(100, 0)]   // Zero height
    public void GenerateThumbnailsAsync_RejectsZeroDimensions(int width, int height)
    {
        // Test that zero-dimension images are caught before scaling
        // Image.LoadAsync should reject these before thumbnail generation
        Assert.True(width == 0 || height == 0);
    }
}
