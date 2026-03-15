// File: PhotoIQPro.Services/Drives/DriveService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Drives;

public class DriveService : IDriveService
{
    // ── File Extensions ─────────────────────────────────────────────────

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".heif", ".avif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg", ".3gp", ".webm"
    };

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".dng", ".raf", ".pef", ".srw"
    };

    private static readonly HashSet<string> AllMediaExtensions =
        PhotoExtensions.Concat(VideoExtensions).Concat(RawExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ── Default Exclusions ──────────────────────────────────────────────

    public IReadOnlyList<string> DefaultExclusions { get; } = new List<string>
    {
        // Windows system
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "MSOCache",

        // User folders unlikely to have photos
        "AppData",
        ".nuget",
        ".dotnet",
        ".vscode",
        ".vs",

        // Dev / source control
        "node_modules",
        ".git",
        "bin",
        "obj",
        "packages",
        "TestResults",
        "__pycache__",

        // Thumbnails & caches
        "thumbnails",
        "Thumbs.db",
        ".cache",
        "BrowserCache",

        // App-specific
        "PhotoIQPro"   // Don't scan our own app data
    }.AsReadOnly();

    // ── GetAvailableDrives ──────────────────────────────────────────────

    public IEnumerable<DriveInfoDto> GetAvailableDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveInfoDto(
                d.Name,
                d.VolumeLabel,
                d.DriveType.ToString(),
                d.TotalSize,
                d.AvailableFreeSpace,
                d.IsReady));
    }

    public DriveInfoDto? GetDriveForPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return null;

        var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
            d.IsReady && d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));

        if (drive is null) return null;

        return new DriveInfoDto(
            drive.Name, drive.VolumeLabel, drive.DriveType.ToString(),
            drive.TotalSize, drive.AvailableFreeSpace, drive.IsReady);
    }

    // ── ScanForMediaAsync ───────────────────────────────────────────────

    public async Task<ScanResult> ScanForMediaAsync(
        string path,
        bool recursive,
        IReadOnlyList<string> excludedFolders,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var files = new List<FoundMediaFile>();
        var foldersScanned = 0;

        // Build a case-insensitive set from the exclusion list
        var exclusionSet = new HashSet<string>(excludedFolders, StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            ScanDirectory(path, recursive, exclusionSet, files, ref foldersScanned, progress, ct);
        }, ct);

        sw.Stop();

        return new ScanResult(
            TotalFiles: files.Count,
            PhotoCount: files.Count(f => f.Category == MediaCategory.Photo),
            VideoCount: files.Count(f => f.Category == MediaCategory.Video),
            RawCount: files.Count(f => f.Category == MediaCategory.Raw),
            TotalSizeBytes: files.Sum(f => f.SizeBytes),
            Files: files,
            Duration: sw.Elapsed);
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    private void ScanDirectory(
        string directory,
        bool recursive,
        HashSet<string> exclusions,
        List<FoundMediaFile> results,
        ref int foldersScanned,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // Check if this folder name is in the exclusion list
            var folderName = Path.GetFileName(directory);
            if (!string.IsNullOrEmpty(folderName) && exclusions.Contains(folderName))
                return;

            // Skip hidden / system folders (but not drive roots)
            var dirInfo = new DirectoryInfo(directory);
            bool isDriveRoot = dirInfo.Parent == null;
            if (!isDriveRoot &&
                (dirInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
                 dirInfo.Attributes.HasFlag(FileAttributes.System)))
                return;

            foldersScanned++;

            // Report progress every folder
            progress?.Report(new ScanProgress(foldersScanned, results.Count, directory));

            // Scan files in this folder
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(file);
                if (!AllMediaExtensions.Contains(ext)) continue;

                try
                {
                    var fi = new FileInfo(file);
                    results.Add(new FoundMediaFile(
                        fi.FullName,
                        fi.Name,
                        fi.DirectoryName ?? directory,
                        ext.ToLowerInvariant(),
                        fi.Length,
                        CategorizeFile(ext)));
                }
                catch (Exception)
                {
                    // Skip files we can't access
                }
            }

            // Recurse into subdirectories
            if (recursive)
            {
                foreach (var subDir in Directory.EnumerateDirectories(directory))
                {
                    ScanDirectory(subDir, true, exclusions, results, ref foldersScanned, progress, ct);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private static MediaCategory CategorizeFile(string extension)
    {
        if (RawExtensions.Contains(extension)) return MediaCategory.Raw;
        if (VideoExtensions.Contains(extension)) return MediaCategory.Video;
        return MediaCategory.Photo;
    }
}
