// File: PhotoIQPro.Services/Drives/DriveService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Drives;

public class DriveService : IDriveService
{
    private static readonly HashSet<string> AllMediaExtensions = SupportedFormats.AllExtensions;

    // ── Default Exclusions ──────────────────────────────────────────────

    /// <summary>
    /// Gets a list of default folder names to exclude from media scans.
    /// </summary>
    /// <remarks>
    /// This list includes Windows system folders (Windows, Program Files, System Volume Information),
    /// user application data (AppData), development folders (.git, node_modules, bin, obj),
    /// and cache directories (thumbnails, .cache, Thumbs.db). Users can add additional custom exclusions.
    /// </remarks>
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

    /// <summary>
    /// Enumerates all drives currently connected and ready.
    /// </summary>
    /// <remarks>
    /// This method queries the system for mounted drives and returns metadata for each one
    /// (name, volume label, type, capacity, free space). Offline or not-ready drives are excluded.
    /// </remarks>
    /// <returns>An enumerable of DriveInfoDto objects for all available drives.</returns>
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

    /// <summary>
    /// Gets drive information for the drive containing a given file path.
    /// </summary>
    /// <remarks>
    /// This method extracts the drive root from the path (e.g., "C:" from "C:\Users\...") and returns
    /// the corresponding DriveInfoDto if the drive is currently connected and ready.
    /// </remarks>
    /// <param name="path">A file or folder path.</param>
    /// <returns>DriveInfoDto if the drive is found and ready; null if the path is invalid or the drive is offline.</returns>
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

    /// <summary>
    /// Scans a directory for supported media files (photos, RAW, video).
    /// </summary>
    /// <remarks>
    /// This method recursively (optionally) enumerates all directories under the given path and collects
    /// supported files (extensions in SupportedFormats.AllExtensions). Folder names matching the exclusion list
    /// are skipped, along with their contents. The scan runs on a thread pool thread to avoid blocking the UI.
    ///
    /// Progress is reported periodically as folders are scanned. The scan gracefully skips inaccessible
    /// directories (permission denied, etc.) and continues.
    /// </remarks>
    /// <param name="path">Root path to scan (folder path).</param>
    /// <param name="recursive">If true, recursively scan all subfolders; if false, scan only the root folder.</param>
    /// <param name="excludedFolders">List of folder names to skip (case-insensitive). Combined with DefaultExclusions.</param>
    /// <param name="progress">Optional progress reporter showing folders scanned and files found so far.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ScanResult with lists of found files, elapsed time, and whether the scan completed fully.</returns>
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
        if (SupportedFormats.RawExtensions.Contains(extension)) return MediaCategory.Raw;
        return MediaCategory.Photo;
    }
}
