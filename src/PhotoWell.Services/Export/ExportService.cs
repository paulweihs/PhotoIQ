using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Services.Export;

public class ExportService : IExportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IThumbnailService    _thumbs;

    private const string ManifestEntry  = "manifest.json";
    private const string MetadataEntry  = "metadata.json";
    private const string PhotosFolder   = "photos/";
    private const string DbEntry        = "photowell.db";
    private const string ThumbnailsEntry = "thumbnails/";
    private const string BundleVersion  = "1.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented         = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ExportService(IServiceScopeFactory scopeFactory, IThumbnailService thumbs)
    {
        _scopeFactory = scopeFactory;
        _thumbs       = thumbs;
    }

    // ── Size estimate ────────────────────────────────────────────────────────

    public Task<long> EstimateBackupSizeAsync(bool includeThumbnails)
    {
        long size = new FileInfo(AppSettings.DatabasePath).Length;
        if (includeThumbnails && Directory.Exists(AppSettings.ThumbnailsPath))
            size += new DirectoryInfo(AppSettings.ThumbnailsPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        return Task.FromResult(size);
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    public async Task CreateBackupAsync(string destPath, bool includeThumbnails,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        // Count entries for progress reporting
        var thumbFiles = includeThumbnails && Directory.Exists(AppSettings.ThumbnailsPath)
            ? Directory.EnumerateFiles(AppSettings.ThumbnailsPath, "*", SearchOption.AllDirectories).ToList()
            : new List<string>();
        int total = 1 + thumbFiles.Count; // 1 = DB
        int done  = 0;

        using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);

        // Database
        progress?.Report(new ExportProgress(total, done, "photowell.db"));
        zip.CreateEntryFromFile(AppSettings.DatabasePath, DbEntry, CompressionLevel.SmallestSize);
        done++;
        progress?.Report(new ExportProgress(total, done, "photowell.db"));

        // Thumbnails (optional)
        if (includeThumbnails)
        {
            var thumbRoot = AppSettings.ThumbnailsPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var file in thumbFiles)
            {
                ct.ThrowIfCancellationRequested();
                var rel = ThumbnailsEntry + file[thumbRoot.Length..].Replace('\\', '/');
                progress?.Report(new ExportProgress(total, done, Path.GetFileName(file)));
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Fastest);
                done++;
            }
        }
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    public async Task RestoreBackupAsync(string zipPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        // Validate ZIP contains photowell.db
        using (var probe = ZipFile.OpenRead(zipPath))
        {
            if (probe.GetEntry(DbEntry) == null)
                throw new InvalidOperationException("This ZIP does not appear to be a PhotoWell backup — photowell.db not found.");
        }

        progress?.Report(new ExportProgress(1, 0, "Closing database connection…"));

        // Release all EF Core connections to the SQLite file.
        SqliteConnection.ClearAllPools();
        await Task.Delay(500, ct); // brief pause for OS to release file handles

        progress?.Report(new ExportProgress(1, 0, "Restoring database…"));

        // Extract db, overwriting current.
        using var zip = ZipFile.OpenRead(zipPath);
        var dbEntry = zip.GetEntry(DbEntry)!;
        dbEntry.ExtractToFile(AppSettings.DatabasePath, overwrite: true);

        // Extract thumbnails if present.
        var thumbEntries = zip.Entries
            .Where(e => e.FullName.StartsWith(ThumbnailsEntry, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
            .ToList();

        int total = 1 + thumbEntries.Count, done = 1;
        progress?.Report(new ExportProgress(total, done, "Database restored."));

        if (thumbEntries.Count > 0)
        {
            Directory.CreateDirectory(AppSettings.ThumbnailsPath);
            foreach (var entry in thumbEntries)
            {
                ct.ThrowIfCancellationRequested();
                var rel  = entry.FullName[ThumbnailsEntry.Length..].Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(AppSettings.ThumbnailsPath, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                done++;
                progress?.Report(new ExportProgress(total, done, Path.GetFileName(dest)));
            }
        }
    }

    // ── Bundle export ────────────────────────────────────────────────────────

    public async Task CreateBundleAsync(IReadOnlyList<Guid> photoIds, string destPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var libRepo = scope.ServiceProvider.GetRequiredService<ILibraryRepository>();

        // Load photos with tags (GetAllWithTagsAsync filters to all, so filter to requested IDs)
        var idSet  = photoIds.ToHashSet();
        var allWithTags = (await repo.GetAllWithTagsAsync()).Where(m => idSet.Contains(m.Id)).ToList();
        int total = allWithTags.Count, done = 0;

        // Load album memberships for these photos
        var albumCounts = await libRepo.GetAlbumPhotoCountsAsync(); // just to confirm albums exist
        var libraries   = await libRepo.GetAllAsync();
        var photoAlbums = new Dictionary<Guid, List<string>>();
        foreach (var lib in libraries)
            foreach (var album in lib.Albums)
            {
                var albumPhotos = await libRepo.GetPhotosByAlbumAsync(album.Id);
                foreach (var ap in albumPhotos.Where(p => idSet.Contains(p.Id)))
                {
                    if (!photoAlbums.TryGetValue(ap.Id, out var list))
                        photoAlbums[ap.Id] = list = new List<string>();
                    list.Add(album.Name);
                }
            }

        var metaRecords = new List<BundlePhotoRecord>(allWithTags.Count);

        using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);

        // Track filenames used in photos/ folder to handle collisions
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var mf in allWithTags)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(total, done, mf.FileName));

            // Resolve unique filename within the archive
            string archiveName = ResolveArchiveName(mf.FileName, usedNames);

            // Copy source file into bundle
            if (File.Exists(mf.FilePath))
                zip.CreateEntryFromFile(mf.FilePath, PhotosFolder + archiveName, CompressionLevel.Fastest);

            // Load faces with person names
            var faces = await repo.GetFacesForPhotoAsync(mf.Id);
            var faceRecords = faces
                .Where(f => f.Person?.Name != null)
                .Select(f => new BundleFaceRecord(f.Person!.Name!, f.X, f.Y, f.Width, f.Height))
                .ToList();

            photoAlbums.TryGetValue(mf.Id, out var albumNames);

            metaRecords.Add(new BundlePhotoRecord(
                Hash:            mf.FileHash ?? "",
                ArchiveFileName: archiveName,
                OriginalPath:    mf.FilePath,
                DateTaken:       mf.DateTaken,
                Rating:          mf.Rating,
                IsFavorite:      mf.IsFavorite,
                UserCaption:     mf.UserCaption,
                AiDescription:   mf.AiDescription,
                UserDescription: mf.UserDescription,
                Tags:            mf.Tags.Select(t => t.Name).ToList(),
                Albums:          albumNames ?? new List<string>(),
                Faces:           faceRecords));

            done++;
        }

        // Write metadata.json
        var metaEntry = zip.CreateEntry(MetadataEntry, CompressionLevel.SmallestSize);
        await using (var stream = metaEntry.Open())
            await JsonSerializer.SerializeAsync(stream, metaRecords, JsonOpts, ct);

        // Write manifest.json
        var manifest = new BundleManifestDto(
            Version:       BundleVersion,
            ExportedAt:    DateTime.UtcNow,
            PhotoCount:    allWithTags.Count,
            SourceMachine: Environment.MachineName);
        var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.SmallestSize);
        await using (var stream = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOpts, ct);

        progress?.Report(new ExportProgress(total, total, "Done"));
    }

    // ── Bundle manifest read ─────────────────────────────────────────────────

    public Task<BundleManifest?> ReadBundleManifestAsync(string bundlePath)
    {
        try
        {
            using var zip   = ZipFile.OpenRead(bundlePath);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry == null) return Task.FromResult<BundleManifest?>(null);
            using var stream = entry.Open();
            var dto = JsonSerializer.Deserialize<BundleManifestDto>(stream, JsonOpts);
            if (dto == null) return Task.FromResult<BundleManifest?>(null);
            return Task.FromResult<BundleManifest?>(
                new BundleManifest(dto.Version, dto.ExportedAt, dto.PhotoCount, dto.SourceMachine));
        }
        catch { return Task.FromResult<BundleManifest?>(null); }
    }

    // ── Bundle import ────────────────────────────────────────────────────────

    public async Task<BundleImportResult> ImportBundleAsync(
        string bundlePath, string photoDestFolder,
        Func<BundleConflict, Task<ConflictResolution>> resolveConflict,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo   = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
        var thumbs = _thumbs;

        // Read metadata from bundle
        using var zip = ZipFile.OpenRead(bundlePath);
        var metaEntry = zip.GetEntry(MetadataEntry)
            ?? throw new InvalidOperationException("Bundle is missing metadata.json");

        List<BundlePhotoRecord> records;
        using (var stream = metaEntry.Open())
            records = JsonSerializer.Deserialize<List<BundlePhotoRecord>>(stream, JsonOpts)
                      ?? throw new InvalidOperationException("Bundle metadata is corrupt");

        Directory.CreateDirectory(photoDestFolder);

        int total    = records.Count, done = 0;
        int imported = 0, merged = 0, skipped = 0, failed = 0;
        var errors   = new List<string>();
        var start    = DateTime.UtcNow;

        foreach (var rec in records)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(total, done, rec.ArchiveFileName));

            try
            {
                var existing = string.IsNullOrEmpty(rec.Hash) ? null : await repo.GetByHashAsync(rec.Hash);

                if (existing == null)
                {
                    // ── New photo: extract file, import with metadata pre-applied ──────
                    var photoEntry = zip.GetEntry(PhotosFolder + rec.ArchiveFileName);
                    if (photoEntry == null) { skipped++; errors.Add($"{rec.ArchiveFileName}: file missing from bundle"); continue; }

                    var destFile = Path.Combine(photoDestFolder, rec.ArchiveFileName);
                    // Avoid overwriting an already-extracted file with the same name
                    destFile = UniqueFilePath(destFile);
                    photoEntry.ExtractToFile(destFile);

                    var mf = new MediaFile
                    {
                        FilePath   = destFile,
                        FileName   = Path.GetFileName(destFile),
                        Extension  = Path.GetExtension(destFile).ToLowerInvariant(),
                        FileSize   = new FileInfo(destFile).Length,
                        FileHash   = rec.Hash,
                        DateTaken  = rec.DateTaken,
                        Rating     = rec.Rating,
                        IsFavorite = rec.IsFavorite,
                        UserCaption     = rec.UserCaption,
                        AiDescription   = rec.AiDescription,
                        UserDescription = rec.UserDescription,
                        IsAnalyzed      = !string.IsNullOrEmpty(rec.AiDescription),
                        AnalysisStatus  = !string.IsNullOrEmpty(rec.AiDescription)
                            ? AnalysisStatus.Complete : AnalysisStatus.Pending,
                    };

                    await repo.AddAsync(mf);
                    await ApplyTagsAsync(repo, mf, rec.Tags);
                    await ApplyFacesAsync(repo, scope, mf, rec.Faces);
                    await thumbs.GenerateThumbnailsAsync(mf, ct);
                    await repo.UpdateAsync(mf);
                    imported++;
                }
                else
                {
                    // ── Existing photo: check for differences ────────────────────────
                    bool differs = existing.Rating          != rec.Rating
                                || existing.IsFavorite      != rec.IsFavorite
                                || existing.UserCaption     != rec.UserCaption
                                || existing.AiDescription   != rec.AiDescription
                                || existing.UserDescription != rec.UserDescription
                                || !existing.Tags.Select(t => t.Name).OrderBy(n => n)
                                       .SequenceEqual(rec.Tags.OrderBy(n => n));

                    if (!differs) { skipped++; continue; }

                    var existingTags = existing.Tags.Select(t => t.Name).ToList();
                    var conflict = new BundleConflict(
                        rec.ArchiveFileName, rec.Hash,
                        existing.Rating, existing.IsFavorite, existing.UserCaption,
                        existing.AiDescription, existing.UserDescription, existingTags,
                        rec.Rating, rec.IsFavorite, rec.UserCaption,
                        rec.AiDescription, rec.UserDescription, rec.Tags);

                    var resolution = await resolveConflict(conflict);
                    if (resolution == ConflictResolution.KeepExisting) { skipped++; continue; }

                    // Apply bundle values to existing record
                    existing.Rating          = rec.Rating;
                    existing.IsFavorite      = rec.IsFavorite;
                    existing.UserCaption     = rec.UserCaption;
                    existing.AiDescription   = rec.AiDescription;
                    existing.UserDescription = rec.UserDescription;
                    if (!string.IsNullOrEmpty(rec.AiDescription))
                    {
                        existing.IsAnalyzed     = true;
                        existing.AnalysisStatus = AnalysisStatus.Complete;
                    }

                    // Replace AI tags with bundle tags (keep user tags)
                    foreach (var t in existing.Tags.Where(t => t.IsAIGenerated).ToList())
                        existing.Tags.Remove(t);
                    await ApplyTagsAsync(repo, existing, rec.Tags);
                    await repo.UpdateAsync(existing);
                    merged++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{rec.ArchiveFileName}: {ex.Message}");
            }

            done++;
        }

        return new BundleImportResult(total, imported, merged, skipped, failed,
            DateTime.UtcNow - start, errors);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ResolveArchiveName(string fileName, Dictionary<string, int> usedNames)
    {
        if (!usedNames.TryGetValue(fileName, out var count))
        {
            usedNames[fileName] = 1;
            return fileName;
        }
        usedNames[fileName] = count + 1;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        return $"{stem}_{count + 1}{ext}";
    }

    private static string UniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        var dir  = Path.GetDirectoryName(path)!;
        int i = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{stem}_{i++}{ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    private static async Task ApplyTagsAsync(IMediaFileRepository repo, MediaFile mf, IReadOnlyList<string> tagNames)
    {
        foreach (var name in tagNames)
        {
            var tag = await repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(),
                TagCategory.Custom, false, 0f);
            if (!mf.Tags.Any(t => t.Id == tag.Id))
                mf.Tags.Add(tag);
        }
    }

    private static async Task ApplyFacesAsync(IMediaFileRepository repo, IServiceScope scope,
        MediaFile mf, IReadOnlyList<BundleFaceRecord> faces)
    {
        if (faces.Count == 0) return;
        var personRepo = scope.ServiceProvider.GetRequiredService<IPersonRepository>();
        var allPersons = (await personRepo.GetAllAsync()).ToList();

        foreach (var fr in faces)
        {
            var person = allPersons.FirstOrDefault(p =>
                string.Equals(p.Name, fr.PersonName, StringComparison.OrdinalIgnoreCase));
            if (person == null)
            {
                person = await personRepo.CreateAsync(new Person { Name = fr.PersonName });
                allPersons.Add(person);
            }
            await repo.AddFaceAsync(new Face
            {
                MediaFileId = mf.Id,
                PersonId    = person.Id,
                X = fr.X, Y = fr.Y, Width = fr.W, Height = fr.H,
            });
        }
        mf.FaceDetectionStatus = FaceDetectionStatus.Complete;
    }
}

// ── Internal DTO types (not part of the public interface) ────────────────────

internal record BundleManifestDto(
    string Version, DateTime ExportedAt, int PhotoCount, string SourceMachine);

internal record BundlePhotoRecord(
    string Hash,
    string ArchiveFileName,
    string OriginalPath,
    DateTime? DateTaken,
    int Rating,
    bool IsFavorite,
    string? UserCaption,
    string? AiDescription,
    string? UserDescription,
    List<string> Tags,
    List<string> Albums,
    List<BundleFaceRecord> Faces);

internal record BundleFaceRecord(
    string PersonName, double X, double Y, double W, double H);
