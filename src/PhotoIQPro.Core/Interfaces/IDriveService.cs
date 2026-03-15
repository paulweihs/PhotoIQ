// File: PhotoIQPro.Core/Interfaces/IDriveService.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoIQPro.Core.Interfaces;

public interface IDriveService
{
    IEnumerable<DriveInfoDto> GetAvailableDrives();
    DriveInfoDto? GetDriveForPath(string path);

    Task<ScanResult> ScanForMediaAsync(
        string path,
        bool recursive,
        IReadOnlyList<string> excludedFolders,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Default folder names that should be excluded from scanning.
    /// </summary>
    IReadOnlyList<string> DefaultExclusions { get; }
}

// ── DTOs ────────────────────────────────────────────────────────────────

public record DriveInfoDto(
    string Name,
    string Label,
    string DriveType,
    long TotalSize,
    long FreeSpace,
    bool IsReady);

public record ScanProgress(
    int FoldersScanned,
    int FilesFound,
    string CurrentFolder);

public record ScanResult(
    int TotalFiles,
    int PhotoCount,
    int VideoCount,
    int RawCount,
    long TotalSizeBytes,
    List<FoundMediaFile> Files,
    TimeSpan Duration);

public record FoundMediaFile(
    string FullPath,
    string FileName,
    string Directory,
    string Extension,
    long SizeBytes,
    MediaCategory Category);

public enum MediaCategory
{
    Photo,
    Video,
    Raw
}