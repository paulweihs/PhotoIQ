using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoWell.Services.Drives;
using Xunit;

namespace PhotoWell.Tests;

/// <summary>Tests for the DriveService: drive enumeration, path resolution, and scan filtering.</summary>
public class DriveServiceTests
{
    // ── GetAvailableDrives ──────────────────────────────────────────────────

    [Fact]
    public void GetAvailableDrives_ReturnsAtLeastOneReadyDrive()
    {
        var service = new DriveService();
        var drives = service.GetAvailableDrives().ToList();

        // System should have at least the OS drive (C: on Windows)
        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.True(d.IsReady, $"Drive {d.Name} should be ready"));
    }

    [Fact]
    public void GetAvailableDrives_EachDriveHasRequiredProperties()
    {
        var service = new DriveService();
        var drives = service.GetAvailableDrives().ToList();

        Assert.All(drives, drive =>
        {
            Assert.NotNull(drive.Name);
            Assert.NotEmpty(drive.Name);
            Assert.True(drive.TotalSize > 0, "TotalSize should be > 0");
            Assert.True(drive.FreeSpace >= 0, "FreeSpace should be >= 0");
            Assert.NotNull(drive.DriveType);
        });
    }

    // ── GetDriveForPath ──────────────────────────────────────────────────────

    [Fact]
    public void GetDriveForPath_ReturnsNullForInvalidPath()
    {
        var service = new DriveService();
        var result = service.GetDriveForPath("");

        Assert.Null(result);
    }

    [Fact]
    public void GetDriveForPath_ReturnsNullForNullPath()
    {
        var service = new DriveService();
        var result = service.GetDriveForPath(null!);

        Assert.Null(result);
    }

    [Fact]
    public void GetDriveForPath_ReturnsValidDriveForSystemPath()
    {
        var service = new DriveService();
        // Use the temp directory, which exists on any Windows system
        string tempPath = Path.GetTempPath();

        var result = service.GetDriveForPath(tempPath);

        // Should return a valid drive (the drive temp folder is on)
        Assert.NotNull(result);
        Assert.NotEmpty(result.Name);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void GetDriveForPath_ReturnsDriveForWindowsPath()
    {
        var service = new DriveService();
        var result = service.GetDriveForPath(@"C:\Windows\System32");

        // C: should exist on Windows
        Assert.NotNull(result);
        Assert.StartsWith("C:", result.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDriveForPath_CaseInsensitive()
    {
        var service = new DriveService();
        var result1 = service.GetDriveForPath(@"C:\Windows");
        var result2 = service.GetDriveForPath(@"c:\windows");

        // Both should return the same drive (case-insensitive)
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result1.Name, result2.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ── DefaultExclusions ────────────────────────────────────────────────────

    [Fact]
    public void DefaultExclusions_ContainsCommonSystemFolders()
    {
        var service = new DriveService();
        var exclusions = service.DefaultExclusions;

        Assert.Contains("Windows", exclusions);
        Assert.Contains("Program Files", exclusions);
        Assert.Contains("AppData", exclusions);
    }

    [Fact]
    public void DefaultExclusions_ContainsDevelopmentFolders()
    {
        var service = new DriveService();
        var exclusions = service.DefaultExclusions;

        Assert.Contains(".git", exclusions);
        Assert.Contains("node_modules", exclusions);
        Assert.Contains("bin", exclusions);
        Assert.Contains("obj", exclusions);
    }

    [Fact]
    public void DefaultExclusions_ContainsCacheFolders()
    {
        var service = new DriveService();
        var exclusions = service.DefaultExclusions;

        Assert.Contains("thumbnails", exclusions);
        Assert.Contains(".cache", exclusions);
    }

    [Fact]
    public void DefaultExclusions_IsReadOnly()
    {
        var service = new DriveService();
        var exclusions = service.DefaultExclusions;

        // Should be read-only (IReadOnlyList)
        Assert.IsAssignableFrom<IReadOnlyList<string>>(exclusions);
    }

    // ── ScanForMediaAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ScanForMediaAsync_ReturnsEmptyForNonexistentPath()
    {
        var service = new DriveService();
        var result = await service.ScanForMediaAsync(
            "/nonexistent/path",
            recursive: true,
            excludedFolders: new List<string>(),
            progress: null,
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task ScanForMediaAsync_FindsFilesInSimpleDirectory()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create test files
            File.WriteAllText(Path.Combine(tempDir, "photo.jpg"), "");
            File.WriteAllText(Path.Combine(tempDir, "image.png"), "");
            File.WriteAllText(Path.Combine(tempDir, "document.txt"), "");  // Not a media file

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: false,
                excludedFolders: new List<string>(),
                progress: null,
                ct: CancellationToken.None);

            // Should find 2 media files (jpg, png) but not the txt file
            Assert.NotEmpty(result.Files);
            Assert.True(result.Files.Count >= 2,
                $"Expected at least 2 media files, found {result.Files.Count}");
            Assert.DoesNotContain(result.Files,
                f => f.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanForMediaAsync_NonRecursiveStaysInRoot()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        string subDir = Path.Combine(tempDir, "subfolder");
        Directory.CreateDirectory(subDir);

        try
        {
            // Create test files
            File.WriteAllText(Path.Combine(tempDir, "root.jpg"), "");
            File.WriteAllText(Path.Combine(subDir, "nested.jpg"), "");

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: false,  // Not recursive
                excludedFolders: new List<string>(),
                progress: null,
                ct: CancellationToken.None);

            // Should find only root.jpg, not nested.jpg
            var filenames = result.Files.Select(f => f.FileName).ToList();
            Assert.Contains("root.jpg", filenames);
            Assert.DoesNotContain("nested.jpg", filenames);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanForMediaAsync_RecursiveIncludesSubfolders()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        string subDir = Path.Combine(tempDir, "subfolder");
        Directory.CreateDirectory(subDir);

        try
        {
            // Create test files
            File.WriteAllText(Path.Combine(tempDir, "root.jpg"), "");
            File.WriteAllText(Path.Combine(subDir, "nested.jpg"), "");

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: true,  // Recursive
                excludedFolders: new List<string>(),
                progress: null,
                ct: CancellationToken.None);

            // Should find both root.jpg and nested.jpg
            var filenames = result.Files.Select(f => f.FileName).ToList();
            Assert.Contains("root.jpg", filenames);
            Assert.Contains("nested.jpg", filenames);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanForMediaAsync_ExcludesFilteredFolders()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        string excludedDir = Path.Combine(tempDir, "excluded");
        string includedDir = Path.Combine(tempDir, "included");
        Directory.CreateDirectory(excludedDir);
        Directory.CreateDirectory(includedDir);

        try
        {
            File.WriteAllText(Path.Combine(excludedDir, "skip.jpg"), "");
            File.WriteAllText(Path.Combine(includedDir, "scan.jpg"), "");

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: true,
                excludedFolders: new List<string> { "excluded" },
                progress: null,
                ct: CancellationToken.None);

            var filenames = result.Files.Select(f => f.FileName).ToList();
            Assert.Contains("scan.jpg", filenames);
            Assert.DoesNotContain("skip.jpg", filenames);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanForMediaAsync_ExclusionIsCaseInsensitive()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        string excludedDir = Path.Combine(tempDir, "Excluded");  // Different case
        Directory.CreateDirectory(excludedDir);

        try
        {
            File.WriteAllText(Path.Combine(excludedDir, "skip.jpg"), "");

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: true,
                excludedFolders: new List<string> { "excluded" },  // lowercase in filter
                progress: null,
                ct: CancellationToken.None);

            var filenames = result.Files.Select(f => f.FileName).ToList();
            Assert.DoesNotContain("skip.jpg", filenames);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanForMediaAsync_ScanResultHasMetadata()
    {
        var service = new DriveService();
        string tempDir = Path.Combine(Path.GetTempPath(), "PhotoWellTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "test.jpg"), "");

            var result = await service.ScanForMediaAsync(
                tempDir,
                recursive: false,
                excludedFolders: new List<string>(),
                progress: null,
                ct: CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.Duration.TotalMilliseconds >= 0);
            Assert.NotEmpty(result.Files);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
