using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MetadataExtractor;
using PhotoWell.Services;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using PhotoWell.Common;
using static PhotoWell.Common.SupportedFormats;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Services.Import;

public class ImportService : IImportService
{
    private readonly IMediaFileRepository       _repo;
    private readonly IThumbnailService          _thumbs;
    private readonly ITaggingService            _tagging;
    private readonly IImageUnderstandingService _vision;
    private readonly IImagePreprocessor         _preprocessor;
    private readonly IAnalysisMetricRepository  _metrics;
    private readonly IFaceService               _faces;

    private const int AnalysisBatchSize = 10;

    /// <summary>
    /// Initializes a new instance of the ImportService.
    /// </summary>
    /// <remarks>
    /// This service orchestrates the import and analysis pipeline, delegating specific tasks to injected services:
    /// - <paramref name="repo"/>: persistence for MediaFile and Tag data
    /// - <paramref name="thumbs"/>: thumbnail generation at multiple sizes
    /// - <paramref name="tagging"/>: CLIP-based embedding and tag suggestions
    /// - <paramref name="vision"/>: LLaVA-based image understanding and description generation
    /// - <paramref name="preprocessor"/>: image resizing and normalization for analysis
    /// - <paramref name="metrics"/>: performance and quality metrics tracking
    /// - <paramref name="faces"/>: face detection and recognition
    /// </remarks>
    /// <param name="repo">Repository for MediaFile persistence.</param>
    /// <param name="thumbs">Service for generating and caching thumbnails.</param>
    /// <param name="tagging">Service for CLIP-based tag suggestion.</param>
    /// <param name="vision">Service for LLaVA-based image understanding.</param>
    /// <param name="preprocessor">Service for image preprocessing (resize, normalize).</param>
    /// <param name="metrics">Repository for tracking analysis metrics.</param>
    /// <param name="faces">Service for face detection.</param>
    public ImportService(IMediaFileRepository repo, IThumbnailService thumbs, ITaggingService tagging,
        IImageUnderstandingService vision, IImagePreprocessor preprocessor,
        IAnalysisMetricRepository metrics, IFaceService faces)
    {
        _repo         = repo;
        _thumbs       = thumbs;
        _tagging      = tagging;
        _vision       = vision;
        _preprocessor = preprocessor;
        _metrics      = metrics;
        _faces        = faces;
    }

    /// <summary>
    /// Clears all AI-generated state on a media file before re-analysis.
    /// </summary>
    private static void StripAiState(MediaFile mf)
    {
        foreach (var t in mf.Tags.Where(t => t.IsAIGenerated).ToList())
            mf.Tags.Remove(t);
        mf.AiDescription  = null;
        mf.AiModelUsed    = null;
        mf.PromptVersion  = null;
        mf.IsAnalyzed     = false;
        mf.AnalysisStatus = AnalysisStatus.Pending;
        mf.FaceDetectionStatus = FaceDetectionStatus.Pending; // Reset face detection to be re-run
    }

    /// <summary>
    /// Determines whether a file extension is supported by PhotoWell.
    /// </summary>
    /// <remarks>
    /// Supported formats include common image formats (JPEG, PNG, GIF, BMP, TIFF),
    /// RAW image formats (CR3, ARW, DNG, RAF, RW2, NEF, ORF, XMP, RWL),
    /// and video formats (MP4, MOV, AVI).
    /// </remarks>
    /// <param name="path">File path or name to check.</param>
    /// <returns>True if the file extension is in the supported formats list; otherwise, false.</returns>
    public bool IsSupportedFile(string path) =>
        AllExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Imports a single photo or video file into the library.
    /// </summary>
    /// <remarks>
    /// This method performs a complete import workflow:
    /// 1. Validates that the file exists and is of a supported type
    /// 2. Checks if the file has already been imported (by path)
    /// 3. Computes SHA256 hash for deduplication and integrity checks
    /// 4. Extracts EXIF metadata (camera, date, ISO, focal length, dimensions)
    /// 5. Generates thumbnails at 150px, 400px, and 800px sizes
    /// 6. Detects faces using the configured face detection service
    /// 7. Runs CLIP tagging analysis (CPU-based, always available)
    /// 8. Skips LLaVA vision analysis (queued for later batch processing)
    ///
    /// All AI processing steps degrade silently — a tagging or face detection failure never aborts the import.
    /// </remarks>
    /// <param name="filePath">Full path to the file to import.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The imported MediaFile if successful; null if the file does not exist, is unsupported, or was already imported.</returns>
    public async Task<MediaFile?> ImportFileAsync(string filePath, CancellationToken ct = default)
    {
        var exists = await _repo.ExistsAsync(filePath);
        if (!File.Exists(filePath) || !IsSupportedFile(filePath) || exists) return null;

        var fi  = new FileInfo(filePath);
        var ext = fi.Extension.ToLowerInvariant();
        var mf  = new MediaFile
        {
            FilePath            = filePath,
            FileName            = fi.Name,
            Extension           = ext,
            FileSize            = fi.Length,
            MediaType           = RawExtensions.Contains(ext) ? MediaType.Raw : MediaType.Photo,
            ImportedWithVersion = AppSettings.Version
        };

        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(filePath))
            mf.FileHash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));

        ExtractMetadata(mf, filePath);

        await _repo.AddAsync(mf);
        await _thumbs.GenerateThumbnailsAsync(mf, ct);

        // Compute perceptual hash using the medium thumbnail (already resized, faster than re-reading the original).
        var thumbForHash = mf.ThumbnailMedium ?? mf.ThumbnailSmall ?? filePath;
        mf.PerceptualHash = PerceptualHashService.ComputeFromFile(thumbForHash);

        // Description reuse — if a near-identical photo already has an AI description, copy it.
        if (mf.PerceptualHash.HasValue)
            await TryCopyDescriptionFromNearDuplicateAsync(mf);

        await _faces.DetectFacesAsync(mf, ct);
        await RunAnalysisAsync(mf, ct, clipOnly: true);
        await _repo.UpdateAsync(mf);
        return mf;
    }

    // ── Perceptual hash description reuse ────────────────────────────────────

    /// <summary>
    /// If a near-duplicate photo (Hamming distance ≤ 8) already has an AI description,
    /// copies that description to <paramref name="mf"/> so the vision LLM step can be skipped.
    /// </summary>
    private async Task TryCopyDescriptionFromNearDuplicateAsync(MediaFile mf)
    {
        if (!mf.PerceptualHash.HasValue) return;

        var candidates = await _repo.GetPerceptualHashCandidatesAsync();
        var donor = candidates.FirstOrDefault(c =>
            c.Id != mf.Id &&
            c.IsAnalyzed &&
            !string.IsNullOrWhiteSpace(c.AiDescription) &&
            PerceptualHashService.HammingDistance(mf.PerceptualHash.Value, c.Hash) <= 8);

        if (donor == default) return;

        mf.AiDescription      = donor.AiDescription;
        mf.AiModelUsed        = donor.AiModelUsed;
        mf.PromptVersion      = donor.PromptVersion;
        mf.IsAnalyzed         = true;
        mf.DateAnalyzed       = DateTime.UtcNow;
        mf.AnalysisStatus     = AnalysisStatus.Complete;
        mf.AnalyzedWithVersion = AppSettings.Version;
        AppLog.Vision($"Reused AI description from near-duplicate for {mf.FileName} (Hamming ≤ 8)");
    }

    // ── Re-analyze ────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-analyzes a single photo, clearing and re-running all AI analysis.
    /// </summary>
    /// <remarks>
    /// This method removes all AI-generated tags, descriptions, and model metadata, then runs a fresh analysis pass.
    /// User-edited descriptions are preserved until explicitly overwritten. The analysis gracefully skips
    /// if the source file is offline (drive not connected).
    /// </remarks>
    /// <param name="mediaFileId">ID of the MediaFile to re-analyze.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if re-analysis completed successfully; false if the file was not found or is offline.</returns>
    public async Task<bool> ReanalyzeFileAsync(Guid mediaFileId, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var mf = await _repo.GetByIdAsync(mediaFileId);
        if (mf == null) return false;

        // Gracefully skip if the source drive is not currently accessible.
        var driveRoot = Path.GetPathRoot(mf.FilePath);
        if (!string.IsNullOrEmpty(driveRoot) && !System.IO.Directory.Exists(driveRoot))
        {
            AppLog.Vision($"Skipped {mf.FileName} — drive offline ({driveRoot})");
            return false;
        }

        StripAiState(mf);
        await _repo.UpdateAsync(mf);

        // Regenerate thumbnails so that EXIF orientation and any image changes are
        // reflected immediately — without this the stale cached thumbnail stays in place.
        var thumbResult = await _thumbs.GenerateThumbnailsAsync(mf, ct);
        if (thumbResult.Success)
        {
            mf.ThumbnailSmall  = thumbResult.SmallPath;
            mf.ThumbnailMedium = thumbResult.MediumPath;
            mf.ThumbnailLarge  = thumbResult.LargePath;
        }

        // Re-detect faces (clears old faces if FaceDetectionStatus was reset to Pending)
        await _faces.DetectFacesAsync(mf, ct);

        // EF Core 8 behavior: after SaveChanges, deleted join rows become Detached.
        // Re-adding the same tag creates a fresh Added join relationship, not a conflict.
        await RunAnalysisAsync(mf, ct);
        await _repo.UpdateAsync(mf);

        progress?.Report(new ImportProgress(1, 1, mf.IsAnalyzed ? 1 : 0, 0, mf.IsAnalyzed ? 0 : 1, mf.FilePath));
        return mf.IsAnalyzed;
    }

    /// <summary>
    /// Re-analyzes multiple photos in batch, with filtering and progress reporting.
    /// </summary>
    /// <remarks>
    /// This method applies AI analysis to a subset of the library with the following filtering options:
    /// - <paramref name="unanalyzedOnly"/>: true to re-analyze only files with AnalysisStatus != Complete; false to re-analyze all.
    /// - <paramref name="skipUserModified"/>: true to skip files with non-null UserDescription (user-edited); false to analyze all.
    ///
    /// Processing is batched in groups of 5 to minimize database churn. Each batch is saved after analysis completes.
    /// Offline files (drive not connected) are silently skipped. An optional callback can be invoked after each file completes.
    /// </remarks>
    /// <param name="unanalyzedOnly">If true, only analyze files with AnalysisStatus != Complete; otherwise analyze all.</param>
    /// <param name="skipUserModified">If true, skip files with non-null UserDescription; otherwise analyze all.</param>
    /// <param name="progress">Optional progress reporter called after each file.</param>
    /// <param name="onPhotoAnalyzed">Optional callback invoked after each photo is analyzed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ImportResult with counts (total photos processed, reanalyzed, skipped, failed, duration, and error list).</returns>
    public async Task<ImportResult> ReanalyzeAllAsync(
        bool unanalyzedOnly,
        bool skipUserModified = false,
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onPhotoAnalyzed = null,
        CancellationToken ct = default)
    {
        // Tags are loaded upfront to avoid a GetByIdAsync per photo just to strip AI tags.
        var all    = await _repo.GetAllWithTagsAsync();
        var photos = (unanalyzedOnly ? all.Where(m => m.AnalysisStatus != AnalysisStatus.Complete) : all)
                     .Where(m => !skipUserModified || m.UserDescription == null)
                     .ToList();
        return await BatchReanalyzeAsync(photos, progress, onPhotoAnalyzed, ct);
    }

    /// <summary>
    /// Re-analyzes photos whose descriptions are outdated relative to the current vision model version.
    /// </summary>
    /// <remarks>
    /// This method identifies photos analyzed with an older version of the vision model (LLaVA)
    /// or using a different model than currently configured in preferences. It then re-analyzes
    /// them with the current model to ensure descriptions remain up-to-date.
    ///
    /// The <paramref name="skipUserModified"/> flag allows skipping photos with user-edited descriptions.
    /// </remarks>
    /// <param name="skipUserModified">If true, skip photos with non-null UserDescription; otherwise analyze all outdated photos.</param>
    /// <param name="progress">Optional progress reporter called after each file.</param>
    /// <param name="onPhotoAnalyzed">Optional callback invoked after each photo is analyzed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ImportResult with counts (outdated photos processed, reanalyzed, skipped, failed, duration, and error list).</returns>
    public async Task<ImportResult> ReanalyzeOutdatedAsync(
        bool skipUserModified = false,
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onPhotoAnalyzed = null,
        CancellationToken ct = default,
        System.Collections.Concurrent.ConcurrentQueue<Guid>? priorityQueue = null)
    {
        var photos = (await _repo.GetOutdatedDescriptionsAsync(
            AppSettings.VisionModelName,
            _vision.PromptVersion))
            .Where(m => !skipUserModified || m.UserDescription == null)
            .ToList();
        return await BatchReanalyzeAsync(photos, progress, onPhotoAnalyzed, ct, priorityQueue);
    }

    /// <summary>
    /// Runs LLaVA vision analysis for a single photo if its status is VisionPending.
    /// </summary>
    /// <remarks>
    /// This method is called by the batch analysis scheduler when a photo's vision analysis is queued (AnalysisStatus = VisionPending).
    /// It checks that:
    /// - The file exists and its drive is online
    /// - Image dimensions are suitable (100×100 minimum, not extreme aspect ratios like 5:1)
    ///
    /// Photos with unsuitable dimensions (logos, banners, icons) are skipped to avoid hallucinated descriptions.
    /// The method skips gracefully if the file is offline; the photo will be retried when the drive reconnects.
    /// </remarks>
    /// <param name="mediaFileId">ID of the MediaFile to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunVisionForPhotoAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        var mf = await _repo.GetByIdAsync(mediaFileId);
        if (mf == null || mf.AnalysisStatus != AnalysisStatus.VisionPending) return;

        var driveRoot = Path.GetPathRoot(mf.FilePath);
        if (!string.IsNullOrEmpty(driveRoot) && !System.IO.Directory.Exists(driveRoot))
        {
            AppLog.Vision($"Skipped vision for {mf.FileName} — drive offline ({driveRoot})");
            return;
        }
        if (!File.Exists(mf.FilePath))
        {
            AppLog.Vision($"Skipped vision for {mf.FileName} — file not found");
            return;
        }

        if (IsUnsuitableForVision(mf.Width, mf.Height))
        {
            AppLog.Vision($"Skipped vision for {mf.FileName} — unsuitable dimensions {mf.Width}×{mf.Height}");
            mf.AnalysisStatus = AnalysisStatus.Complete;
            mf.AiDescription  = string.Empty;
            await _repo.UpdateAsync(mf);
            return;
        }

        bool needsPreprocess = mf.MediaType == MediaType.Raw ||
                               (mf.MediaType == MediaType.Photo && !NativeFormats.Contains(Path.GetExtension(mf.FilePath)));

        PreparedImage? prepared = null;
        string visionPath;

        // Prefer the pre-generated large thumbnail (800px JPEG) — already on disk, smaller I/O,
        // no temp-file creation needed. The vision model rescales internally anyway.
        if (!string.IsNullOrEmpty(mf.ThumbnailLarge) && File.Exists(mf.ThumbnailLarge))
        {
            visionPath = mf.ThumbnailLarge;
        }
        else if (needsPreprocess)
        {
            prepared = await _preprocessor.PrepareAsync(mf.FilePath, ct);
            if (prepared == null)
            {
                AppLog.Vision($"Skipped vision for {mf.FileName} — preprocessor returned null");
                mf.AnalysisStatus = AnalysisStatus.Complete;
                mf.AiDescription  = string.Empty;
                await _repo.UpdateAsync(mf);
                return;
            }
            visionPath = prepared.Path;
        }
        else
        {
            visionPath = mf.FilePath;
        }

        try
        {
            AppLog.Vision($"Background vision: sending {visionPath} to vision model...");
            var understanding = await _vision.AnalyzeImageAsync(visionPath, ct);
            if (!string.IsNullOrEmpty(understanding.Description))
            {
                if (IsLoopingDescription(understanding.Description))
                {
                    AppLog.Vision($"Loop detected for {mf.FileName} — discarding");
                    mf.AiDescription = string.Empty;
                }
                else
                {
                    mf.AiDescription = understanding.Description;
                    mf.AiModelUsed   = AppSettings.VisionModelName;
                    mf.PromptVersion = _vision.PromptVersion;
                }
            }
            else
            {
                AppLog.Vision($"Vision returned empty for {mf.FileName}");
                mf.AiDescription = string.Empty;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Vision($"EXCEPTION in background vision for {mf.FileName}: {ex.GetType().Name}: {ex.Message}");
            mf.AiDescription = string.Empty;
        }
        finally { prepared?.Dispose(); }

        mf.AnalysisStatus      = AnalysisStatus.Complete;
        mf.AnalyzedWithVersion = AppSettings.Version;
        await _repo.UpdateAsync(mf);
    }

    /// <summary>
    /// Regenerates all thumbnails in the library.
    /// </summary>
    /// <remarks>
    /// This method re-creates thumbnail images at all sizes (150px, 400px, 800px) for every photo in the library.
    /// It can be useful after upgrading thumbnail generation logic or recovering from corrupted thumbnails.
    /// Photos with missing files are silently skipped.
    /// </remarks>
    /// <param name="progress">Optional progress reporter called after each file.</param>
    /// <param name="onThumbnailRegenerated">Optional callback invoked after each thumbnail is successfully regenerated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ImportResult with counts (total photos, regenerated, skipped, failed, duration, and error list).</returns>
    /// <summary>
    /// Returns true if a thumbnail regeneration checkpoint exists (i.e. a previous run was interrupted).
    /// </summary>
    public static bool HasThumbnailRegenCheckpoint()
        => File.Exists(PhotoWell.Common.AppSettings.ThumbnailRegenCheckpointPath);

    public async Task<ImportResult> RegenerateThumbnailsAsync(
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onThumbnailRegenerated = null,
        CancellationToken ct = default,
        bool resume = false)
    {
        var checkpointPath = PhotoWell.Common.AppSettings.ThumbnailRegenCheckpointPath;
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        var all    = (await _repo.GetAllAsync()).ToList();

        // Resume: skip photos already processed in the previous run.
        int startIndex = 0;
        if (resume && File.Exists(checkpointPath))
        {
            var savedId = await File.ReadAllTextAsync(checkpointPath, ct);
            if (Guid.TryParse(savedId.Trim(), out var lastId))
            {
                var lastIdx = all.FindIndex(m => m.Id == lastId);
                if (lastIdx >= 0) startIndex = lastIdx + 1;
            }
        }
        else
        {
            // Fresh run — clear any stale checkpoint.
            if (File.Exists(checkpointPath)) File.Delete(checkpointPath);
        }

        int total = all.Count, processed = startIndex, updated = 0, skipped = 0, failed = 0;

        for (int i = startIndex; i < all.Count; i++)
        {
            var mf = all[i];
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(mf.FilePath)) { skipped++; }
                else
                {
                    var result = await _thumbs.GenerateThumbnailsAsync(mf, ct);
                    if (result.Success)
                    {
                        await _repo.UpdateAsync(mf);
                        updated++;
                        onThumbnailRegenerated?.Invoke(mf);
                        // Write checkpoint after each successful save so we can resume here.
                        await File.WriteAllTextAsync(checkpointPath, mf.Id.ToString(), CancellationToken.None);
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{mf.FileName}: {result.Error}");
                        AppLog.Vision($"Thumbnail regen failed for {mf.FileName}: {result.Error}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{mf.FileName}: {ex.Message}");
                AppLog.Vision($"Thumbnail regen exception for {mf.FileName}: {ex.GetType().Name}: {ex.Message}");
            }
            processed++;
            progress?.Report(new ImportProgress(total, processed, updated, skipped, failed, mf.FilePath));
        }

        // Clear checkpoint on successful completion.
        if (processed >= total && File.Exists(checkpointPath))
            File.Delete(checkpointPath);

        return new ImportResult(total, updated, skipped, failed, DateTime.UtcNow - start, errors);
    }

    // ── EXIF Backfill ─────────────────────────────────────────────────────────

    /// <summary>
    /// Re-extracts EXIF metadata for a single photo from its file.
    /// </summary>
    /// <remarks>
    /// This method re-reads EXIF data from the source file and updates the MediaFile with:
    /// - Camera make and model
    /// - Date taken (with fallback chains for RAW/DNG and filename-based extraction)
    /// - Aperture (f-stop), shutter speed, ISO, and focal length
    /// - Image dimensions (width and height)
    ///
    /// The method skips gracefully if the file is not accessible. Extraction failures are logged but do not throw.
    /// </remarks>
    /// <param name="mediaFileId">ID of the MediaFile to backfill.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the file exists and extraction succeeded; false otherwise.</returns>
    public async Task<bool> BackfillExifAsync(Guid mediaFileId, CancellationToken ct = default)
    {
        var mf = await _repo.GetByIdAsync(mediaFileId);
        if (mf == null) return false;

        // Gracefully skip if the source file is not currently accessible.
        if (!File.Exists(mf.FilePath))
        {
            AppLog.Vision($"Skipped EXIF backfill for {mf.FileName} — file not found ({mf.FilePath})");
            return false;
        }

        try
        {
            ExtractMetadata(mf, mf.FilePath);
            await _repo.UpdateAsync(mf);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Vision($"EXIF backfill failed for {mf.FileName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-extracts EXIF metadata for all photos in the library in batch.
    /// </summary>
    /// <remarks>
    /// This method iterates through the entire library, re-reading EXIF data from each file's source.
    /// Processing is batched in groups of 10 to minimize database churn and improve performance.
    /// Photos with missing files are silently skipped. An optional callback can be invoked after each batch is saved.
    /// </remarks>
    /// <param name="progress">Optional progress reporter called after each file.</param>
    /// <param name="onExifBackfilled">Optional callback invoked after each batch of EXIF data is successfully saved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ImportResult with counts (total photos, updated, skipped, failed, duration, and error list).</returns>
    public async Task<ImportResult> BackfillExifAllAsync(
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onExifBackfilled = null,
        CancellationToken ct = default)
    {
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        var all    = (await _repo.GetAllAsync()).ToList();

        const int BatchSize = 10;
        int total = all.Count, processed = 0, updated = 0, skipped = 0, failed = 0;
        var batchBuffer = new List<MediaFile>(BatchSize);

        async Task FlushBatchAsync()
        {
            if (batchBuffer.Count == 0) return;
            await _repo.BatchUpdateAsync(batchBuffer);
            foreach (var m in batchBuffer)
                onExifBackfilled?.Invoke(m);
            batchBuffer.Clear();
        }

        foreach (var mf in all)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(mf.FilePath))
            {
                skipped++;
            }
            else
            {
                try
                {
                    ExtractMetadata(mf, mf.FilePath);
                    batchBuffer.Add(mf);
                    updated++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{mf.FileName}: {ex.Message}");
                    AppLog.Vision($"EXIF backfill failed for {mf.FileName}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            processed++;
            progress?.Report(new ImportProgress(total, processed, updated, skipped, failed, mf.FilePath));

            // Flush batch when full or at end
            if (batchBuffer.Count >= AnalysisBatchSize || processed == total)
                await FlushBatchAsync();
        }

        return new ImportResult(total, updated, skipped, failed, DateTime.UtcNow - start, errors);
    }

    /// <summary>
    /// Imports all supported files from a folder, with optional recursive traversal.
    /// </summary>
    /// <remarks>
    /// This method enumerates files in the specified folder (and its subfolders if <paramref name="recursive"/> is true),
    /// filters by supported format, and imports each file using ImportFileAsync. Express tier users will hit an import
    /// limit (~25,000 images), and the method will stop gracefully when the limit is reached, reporting limitReached in the result.
    ///
    /// Progress is reported after each file, and an optional callback can be invoked after each successful import.
    /// Errors during import are collected and reported in the result; they do not abort the folder scan.
    /// </remarks>
    /// <param name="folder">Full path to the folder to import from.</param>
    /// <param name="recursive">If true, recursively scan subfolders; if false, scan only the top-level folder.</param>
    /// <param name="progress">Optional progress reporter called after each file.</param>
    /// <param name="onPhotoImported">Optional callback invoked after each photo is successfully imported.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ImportResult with counts (total files found, imported, skipped, failed, duration, error list, and limitReached flag).</returns>
    public async Task<ImportResult> ImportFolderAsync(string folder, bool recursive, IProgress<ImportProgress>? progress = null, Action<MediaFile>? onPhotoImported = null, CancellationToken ct = default)
    {
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        var files  = System.IO.Directory.EnumerateFiles(folder, "*.*",
                         recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedFile).ToList();

        // Express tier: enforce the library cap.
        bool expressMode  = UserPreferences.Current.IsExpressMode;
        int  libraryCount = expressMode ? await _repo.CountAsync() : 0;
        bool limitReached = false;

        int total = files.Count, processed = 0, imported = 0, skipped = 0, failed = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            if (expressMode && libraryCount + imported >= AppSettings.ExpressLibraryLimit)
            {
                limitReached = true;
                break;
            }

            try
            {
                var r = await ImportFileAsync(f, ct);
                if (r != null) { imported++; onPhotoImported?.Invoke(r); } else skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                var msg   = ex.Message;
                var inner = ex.InnerException;
                while (inner != null) { msg += " -> " + inner.Message; inner = inner.InnerException; }
                errors.Add($"{Path.GetFileName(f)}: {msg}");
            }
            processed++;
            progress?.Report(new ImportProgress(total, processed, imported, skipped, failed, f));
        }
        return new ImportResult(total, imported, skipped, failed, DateTime.UtcNow - start, errors)
        {
            LimitReached = limitReached
        };
    }

    // ── AI analysis ───────────────────────────────────────────────────────────

    // ── Analysis helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Runs CLIP tagging pipeline: generates tag predictions, adds tags to media file,
    /// includes holiday tags based on date, and stores image embedding for semantic search.
    /// Commits CLIP results to database before vision analysis to ensure durability.
    /// </summary>
    private async Task RunClipPipelineAsync(MediaFile mf, string analysisPath, CancellationToken ct)
    {
        // CLIP tagging — use preprocessed path for RAW so CLIP sees a real image
        try
        {
            var predictions = await _tagging.GenerateTagsAsync(analysisPath, ct);
            AppLog.Vision($"CLIP: {predictions.Count} predictions for {mf.FileName}");
            foreach (var p in predictions)
            {
                var tag = await _repo.GetOrCreateTagAsync(p.Label, p.Label.ToLowerInvariant(), p.Category, true, p.Confidence);
                if (!mf.Tags.Any(t => t.Id == tag.Id)) mf.Tags.Add(tag);
            }
            if (predictions.Count > 0) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }

            // Date-based holiday tags — applied regardless of visual content.
            if (mf.DateTaken.HasValue)
            {
                var holidayName = mf.DateTaken.Value.GetHolidayName();
                if (holidayName != null)
                {
                    var tag = await _repo.GetOrCreateTagAsync(holidayName, holidayName, TagCategory.Activity, true, 1.0f);
                    if (!mf.Tags.Any(t => t.Id == tag.Id)) mf.Tags.Add(tag);
                    if (!mf.IsAnalyzed) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Vision($"CLIP EXCEPTION for {mf.FileName} — {ex.GetType().Name}: {ex.Message}"); }

        // Store CLIP image embedding for semantic search (non-critical)
        try
        {
            var emb = await _tagging.GetImageEmbeddingAsync(analysisPath, ct);
            if (emb != null)
            {
                var bytes = new byte[emb.Length * 4];
                Buffer.BlockCopy(emb, 0, bytes, 0, bytes.Length);
                mf.ClipEmbeddingBytes = bytes;
            }
        }
        catch { /* non-critical */ }

        // Commit CLIP tags + embedding to SQLite before invoking Ollama.
        // Ollama failure, loop detection, or timeout must never discard CLIP results.
        await _repo.UpdateAsync(mf);
    }

    /// <summary>
    /// Runs LLaVA vision inference on a photo with loop detection.
    /// Returns true if the description was accepted and GPU was used; false if loop-detected or empty.
    /// Mutates mf.AiDescription, mf.AiModelUsed, mf.PromptVersion, mf.IsAnalyzed, mf.DateAnalyzed.
    /// </summary>
    private async Task<bool> RunVisionInferenceAsync(MediaFile mf, string visionPath, CancellationToken ct)
    {
        AppLog.Vision($"Sending {visionPath} to vision model...");
        var understanding = await _vision.AnalyzeImageAsync(visionPath, ct);
        if (!string.IsNullOrEmpty(understanding.Description))
        {
            if (IsLoopingDescription(understanding.Description))
            {
                var preview = understanding.Description.Length > 200
                    ? understanding.Description[..200] + "…"
                    : understanding.Description;
                AppLog.Vision($"Loop detected in description for {mf.FileName} — discarding. Content: {preview}");
                mf.AiDescription = null;
                return false;
            }
            mf.AiDescription = understanding.Description;
            mf.AiModelUsed   = AppSettings.VisionModelName;
            mf.PromptVersion = _vision.PromptVersion;
            if (!mf.IsAnalyzed) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }
            return true;
        }
        AppLog.Vision($"Vision returned empty description for {mf.FileName}");
        mf.AiDescription = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves the image path to use for vision analysis with 4-priority fallback chain:
    /// 1. Pre-generated thumbnail (800px, already on disk)
    /// 2. Reuse CLIP preprocessing result
    /// 3. Skip vision if preprocessing was needed but failed
    /// 4. Preprocess native JPEG on demand
    ///
    /// Returns tuple: (visionPath, ownedPrepared, preprocessingMs)
    /// - ownedPrepared is null unless priority 4 applies (caller must dispose)
    /// - preprocessingMs is 0 unless priority 4 applies
    /// </summary>
    private async Task<(string? visionPath, PreparedImage? ownedPrepared, long preprocessingMs)> ResolveVisionPathAsync(
        MediaFile mf, PreparedImage? clipPrepared, bool needsPreprocess, CancellationToken ct)
    {
        // Priority 1: existing large thumbnail (already on disk, smaller I/O)
        if (!string.IsNullOrEmpty(mf.ThumbnailLarge) && File.Exists(mf.ThumbnailLarge))
            return (mf.ThumbnailLarge, null, 0);

        // Priority 2: reuse CLIP's prepared image
        if (clipPrepared != null)
            return (clipPrepared.Path, null, 0);

        // Priority 3: preprocessing was needed but failed — skip vision
        if (needsPreprocess)
        {
            AppLog.Vision($"SKIP vision for {mf.FileName} — preprocessor returned null");
            mf.AiDescription = string.Empty;
            return (null, null, 0);
        }

        // Priority 4: native JPEG — preprocess fresh for Ollama
        AppLog.Vision($"Preprocessing {mf.FileName} for vision...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var owned = await _preprocessor.PrepareAsync(mf.FilePath, ct);
        return (owned?.Path ?? mf.FilePath, owned, sw.ElapsedMilliseconds);
    }

    // ── Preprocessing helper ─────────────────────────────────────────────────
    // File I/O only — no DB access — safe to run concurrently with Ollama inference.

    private async Task<(string analysisPath, PreparedImage? prepared)> PreprocessFileAsync(
        MediaFile mf, CancellationToken ct)
    {
        bool needsPreprocess = mf.MediaType == MediaType.Raw ||
                               (mf.MediaType == MediaType.Photo && !NativeFormats.Contains(Path.GetExtension(mf.FilePath)));
        if (!needsPreprocess)
            return (mf.FilePath, null);

        AppLog.Vision($"Lookahead: preprocessing {mf.FileName}...");
        var prepared = await _preprocessor.PrepareAsync(mf.FilePath, ct);
        return (prepared?.Path ?? mf.FilePath, prepared);
    }

    private async Task FlushBatchAsync(List<MediaFile> buffer, Action<MediaFile>? onPhotoAnalyzed)
    {
        if (buffer.Count == 0) return;
        await _repo.BatchUpdateAsync(buffer);
        foreach (var m in buffer)
            onPhotoAnalyzed?.Invoke(m);
        buffer.Clear();
    }

    /// <summary>
    /// Shared batch reanalysis loop used by ReanalyzeAllAsync and ReanalyzeOutdatedAsync.
    /// Processes photos with lookahead preprocessing, batched updates, and optional progress reporting.
    /// </summary>
    private async Task<ImportResult> BatchReanalyzeAsync(
        List<MediaFile> photos,
        IProgress<ImportProgress>? progress,
        Action<MediaFile>? onPhotoAnalyzed,
        CancellationToken ct,
        System.Collections.Concurrent.ConcurrentQueue<Guid>? priorityQueue = null)
    {
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        int total = photos.Count, processed = 0, reanalyzed = 0, skipped = 0, failed = 0;
        var batchBuffer    = new List<MediaFile>(AnalysisBatchSize);
        var alreadyDone    = new HashSet<Guid>();

        Task<(string, PreparedImage?)>? lookaheadTask =
            photos.Count > 0 ? PreprocessFileAsync(photos[0], ct) : null;

        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Drain the priority queue before processing the next scheduled photo.
            // Each priority item gets full re-analysis via ReanalyzeFileAsync and is
            // then marked so it's skipped when the normal batch reaches it.
            if (priorityQueue != null)
            {
                while (priorityQueue.TryDequeue(out var priorityId))
                {
                    if (alreadyDone.Contains(priorityId)) continue;
                    alreadyDone.Add(priorityId);
                    AppLog.Vision($"[Priority] Processing {priorityId} ahead of batch");
                    try
                    {
                        var ok = await ReanalyzeFileAsync(priorityId, ct: ct);
                        if (ok) reanalyzed++; else skipped++;
                        var refreshed = await _repo.GetByIdAsync(priorityId);
                        if (refreshed != null) onPhotoAnalyzed?.Invoke(refreshed);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        errors.Add($"(priority) {priorityId}: {ex.Message}");
                        AppLog.Error($"Priority re-analysis failed for {priorityId}: {ex.GetType().Name}: {ex.Message}");
                    }
                    progress?.Report(new ImportProgress(total, processed, reanalyzed, skipped, failed, priorityId.ToString()));
                }
            }

            ct.ThrowIfCancellationRequested();
            var mf = photos[i];

            var (analysisPath, prepared) = lookaheadTask != null
                ? await lookaheadTask
                : await PreprocessFileAsync(mf, ct);

            lookaheadTask = i + 1 < photos.Count
                ? PreprocessFileAsync(photos[i + 1], ct)
                : null;

            // Already handled as a priority item — discard the preprocessed result and skip.
            if (alreadyDone.Contains(mf.Id))
            {
                prepared?.Dispose();
                processed++;
                progress?.Report(new ImportProgress(total, processed, reanalyzed, skipped, failed, mf.FilePath));
                continue;
            }

            if (!File.Exists(mf.FilePath))
            {
                var root = Path.GetPathRoot(mf.FilePath);
                bool driveOffline = !string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root);
                AppLog.Vision(driveOffline
                    ? $"Skipped {mf.FileName} — drive offline ({root})"
                    : $"Skipped {mf.FileName} — file not found");
                prepared?.Dispose();
                skipped++;
            }
            else
            {
                StripAiState(mf);
                await _repo.UpdateAsync(mf);
                try
                {
                    await RunAnalysisAsync(mf, ct, analysisPath, prepared);
                    if (mf.IsAnalyzed) reanalyzed++; else skipped++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{mf.FileName}: {ex.Message}");
                    mf.AnalysisStatus = AnalysisStatus.Pending;
                    AppLog.Error($"Analysis failed for '{mf.FilePath}': {ex.GetType().Name}: {ex.Message}");
                }
                finally { prepared?.Dispose(); }

                batchBuffer.Add(mf);
                if (batchBuffer.Count >= AnalysisBatchSize)
                    await FlushBatchAsync(batchBuffer, onPhotoAnalyzed);
            }

            processed++;
            progress?.Report(new ImportProgress(total, processed, reanalyzed, skipped, failed, mf.FilePath));
        }

        await FlushBatchAsync(batchBuffer, onPhotoAnalyzed);
        return new ImportResult(total, reanalyzed, skipped, failed, DateTime.UtcNow - start, errors);
    }

    private async Task RunAnalysisAsync(
        MediaFile mf,
        CancellationToken ct,
        string? preComputedAnalysisPath = null,
        PreparedImage? preComputedPrepared = null,
        bool clipOnly = false)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // Persist Processing state immediately so a crash mid-analysis is detectable at next startup.
        mf.AnalysisStatus = AnalysisStatus.Processing;
        await _repo.UpdateAsync(mf);

        // Use lookahead result if available; otherwise preprocess here.
        PreparedImage? ownedPrepared = null;
        string analysisPath = preComputedAnalysisPath ?? mf.FilePath;
        long preprocessingMs = 0;
        long inferenceMs     = 0;
        bool gpuUsed         = false;

        bool needsPreprocess = mf.MediaType == MediaType.Raw ||
                               (mf.MediaType == MediaType.Photo && !NativeFormats.Contains(Path.GetExtension(mf.FilePath)));

        if (preComputedAnalysisPath == null && needsPreprocess)
        {
            AppLog.Vision($"Preprocessing {mf.FileName}...");
            var swPre = System.Diagnostics.Stopwatch.StartNew();
            ownedPrepared   = await _preprocessor.PrepareAsync(mf.FilePath, ct);
            preprocessingMs = swPre.ElapsedMilliseconds;
            if (ownedPrepared != null)
                analysisPath = ownedPrepared.Path;
            else
                AppLog.Vision($"Preprocessor returned null for {mf.FileName} — will try original path");
        }

        // The effective prepared image: lookahead-supplied or locally created.
        var prepared = preComputedPrepared ?? ownedPrepared;

        try
        {
            var swInference = System.Diagnostics.Stopwatch.StartNew();

            // Fire CLIP pipeline immediately — it runs on CPU via ONNX Runtime.
            var clipTask = RunClipPipelineAsync(mf, analysisPath, ct);

            // While CLIP runs on CPU, resolve the vision path and start Ollama inference on the GPU.
            // CLIP (CPU/ONNX) and Vision (GPU/Ollama) use separate hardware — true parallelism.
            PreparedImage? ollamaPrepared = null;
            Task<bool> visionTask = Task.FromResult(false);
            if (!clipOnly && (mf.MediaType == MediaType.Photo || mf.MediaType == MediaType.Raw))
            {
                try
                {
                    var (visionPath, owned, additionalMs) = await ResolveVisionPathAsync(mf, prepared, needsPreprocess, ct);
                    ollamaPrepared   = owned;
                    preprocessingMs += additionalMs;

                    if (visionPath != null && IsUnsuitableForVision(mf.Width, mf.Height))
                    {
                        AppLog.Vision($"Skipped vision for {mf.FileName} — unsuitable dimensions {mf.Width}×{mf.Height}");
                        mf.AiDescription = string.Empty;
                    }
                    else if (visionPath != null)
                    {
                        visionTask = RunVisionInferenceAsync(mf, visionPath, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Vision($"EXCEPTION resolving vision path for {mf.FileName} — {ex.GetType().Name}: {ex.Message}");
                    mf.AiDescription = string.Empty;
                }
            }

            try
            {
                // Await CLIP first — its UpdateAsync is the durability checkpoint for tags/embedding.
                await clipTask;
                // Vision was running concurrently; await its result (likely already done or near done).
                try   { gpuUsed = await visionTask; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Vision($"EXCEPTION in vision inference for {mf.FileName} — {ex.GetType().Name}: {ex.Message}");
                    mf.AiDescription = string.Empty;
                }
            }
            finally { ollamaPrepared?.Dispose(); }

            inferenceMs = swInference.ElapsedMilliseconds - preprocessingMs;
        }
        finally
        {
            // Only dispose what RunAnalysisAsync itself created — not the lookahead-supplied image
            // (callers that supply preComputedPrepared are responsible for its disposal).
            ownedPrepared?.Dispose();
        }

        // clipOnly = CLIP done but vision still pending; Express mode = no vision ever, go straight to Complete.
        mf.AnalysisStatus = (clipOnly && !UserPreferences.Current.IsExpressMode)
            ? AnalysisStatus.VisionPending
            : AnalysisStatus.Complete;
        if (mf.AnalysisStatus == AnalysisStatus.Complete)
            mf.AnalyzedWithVersion = AppSettings.Version;
        // Mark analyzed at the pipeline boundary — not per-branch — so loop-detected,
        // empty-description, or 0-prediction photos aren't counted as skipped.
        if (!mf.IsAnalyzed) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }
        swTotal.Stop();

        // Record metrics — intentional fire-and-forget; never block the analysis pipeline.
        // Errors are logged but never propagated — a metrics failure must not abort an import.
        // Use a linked token with a 10 s cap so this doesn't outlive app shutdown.
        var metricsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        metricsCts.CancelAfter(TimeSpan.FromSeconds(10));
        _ = RecordMetricsSafeAsync(new AnalysisMetric
        {
            PhotoId           = mf.Id,
            ModelName         = gpuUsed ? "clip+llama3.2-vision" : "clip",
            ImageWidth        = mf.Width,
            ImageHeight       = mf.Height,
            FileFormat        = mf.Extension.TrimStart('.').ToLowerInvariant(),
            FileSizeBytes     = mf.FileSize,
            PreprocessingMs   = preprocessingMs,
            InferenceMs       = Math.Max(0, inferenceMs),
            TotalAnalysisMs   = swTotal.ElapsedMilliseconds,
            TagCount          = mf.Tags.Count,
            DescriptionLength = mf.AiDescription?.Length ?? 0,
            GpuUsed           = gpuUsed,
            AnalyzedAt        = DateTime.UtcNow,
        }, metricsCts);
    }


    private async Task RecordMetricsSafeAsync(AnalysisMetric metric, CancellationTokenSource cts)
    {
        AppLog.Info($"[METRICS] RecordMetricsSafeAsync starting for photo {metric.PhotoId} on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
        try
        {
            await _metrics.AddAsync(metric);
            AppLog.Info($"[METRICS] RecordMetricsSafeAsync done for photo {metric.PhotoId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var chain = ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                chain += " → " + inner.Message;
            AppLog.Error($"[METRICS] RecordMetricsSafeAsync FAILED for photo {metric.PhotoId}: {chain}\n{ex.StackTrace}");
        }
        finally
        {
            cts.Dispose();
        }
    }

    // ── Full metadata read (on-demand) ────────────────────────────────────────

    /// <summary>
    /// Reads all EXIF and metadata tags from a file on-demand.
    /// </summary>
    /// <remarks>
    /// This method uses MetadataExtractor to extract all available EXIF, IPTC, XMP, and other metadata directories
    /// from a file. It is called when the user inspects metadata in the detail panel and can be expensive for files with
    /// extensive metadata. The operation is run on a thread pool thread to avoid blocking the UI.
    /// </remarks>
    /// <param name="filePath">Full path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of MetadataTag objects representing all extracted metadata.</returns>
    public Task<IReadOnlyList<MetadataTag>> ReadAllTagsAsync(string filePath, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<MetadataTag>>(() =>
        {
            var results = new List<MetadataTag>();
            try
            {
                foreach (var dir in ImageMetadataReader.ReadMetadata(filePath))
                    foreach (var tag in dir.Tags)
                    {
                        var value = dir.GetDescription(tag.Type) ?? tag.Description;
                        if (!string.IsNullOrWhiteSpace(value))
                            results.Add(new MetadataTag(dir.Name, tag.Name, value));
                    }
            }
            catch (Exception ex)
            {
                // Partial results are valid — the file may be readable up to the point of error.
                AppLog.Error($"Metadata read failed for '{System.IO.Path.GetFileName(filePath)}': {ex.GetType().Name}: {ex.Message}");
            }
            return results;
        }, ct);

    // ── EXIF extraction ───────────────────────────────────────────────────────

    private static void ExtractMetadata(MediaFile mf, string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null)
            {
                mf.CameraMake  = ifd0.GetDescription(ExifDirectoryBase.TagMake);
                mf.CameraModel = ifd0.GetDescription(ExifDirectoryBase.TagModel);
            }

            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (sub != null)
            {
                if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                    mf.DateTaken = dt;

                if (sub.TryGetDouble(ExifDirectoryBase.TagFNumber, out var fn) && fn > 0)
                    mf.Aperture = fn;

                var shutterDesc = sub.GetDescription(ExifDirectoryBase.TagExposureTime);
                if (!string.IsNullOrEmpty(shutterDesc))
                    mf.ShutterSpeed = shutterDesc.Replace(" sec", "").Trim();

                if (sub.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso))
                    mf.ISO = iso;

                if (sub.TryGetDouble(ExifDirectoryBase.TagFocalLength, out var fl) && fl > 0)
                    mf.FocalLength = fl;

                if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && ew > 0)
                    mf.Width = ew;
                if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh) && eh > 0)
                    mf.Height = eh;
            }

            // Date fallback chain for RAW/DNG files where SubIFD may be structured differently.
            // Canon CRW→DNG and other conversions sometimes store DateTimeOriginal in IFD0
            // or use TagDateTime instead of TagDateTimeOriginal.
            if (mf.DateTaken == null)
            {
                foreach (var dir in dirs.OfType<ExifIfd0Directory>())
                {
                    if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) ||
                        dir.TryGetDateTime(ExifDirectoryBase.TagDateTime, out dt))
                    {
                        mf.DateTaken = dt;
                        break;
                    }
                }
            }

            // Date fallback 3 — parse YYYYMMDD from filename (e.g. CRW_20051124.dng, IMG_20230415_123456.jpg)
            if (mf.DateTaken == null)
            {
                var stem = Path.GetFileNameWithoutExtension(mf.FileName);
                if (TryParseDateFromString(stem, out var dt))
                    mf.DateTaken = dt;
            }

            // Date fallback 4 — parse YYYYMMDD from parent folder name (e.g. 2005-11-24, 20051124)
            if (mf.DateTaken == null)
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                if (!string.IsNullOrEmpty(folder) && TryParseDateFromString(folder, out var dt))
                    mf.DateTaken = dt;
            }

            // Fallback: read dimensions from JPEG segment if not in EXIF SubIFD
            if (mf.Width == 0 || mf.Height == 0)
            {
                var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
                if (jpeg != null)
                {
                    if (jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jw > 0)
                        mf.Width = jw;
                    if (jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) && jh > 0)
                        mf.Height = jh;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"ExtractMetadata failed for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if image dimensions are unsuitable for vision analysis (logos, banners, icons).
    /// Returns true if either dimension &lt; 100px or aspect ratio exceeds 5:1.
    /// </summary>
    private static bool IsUnsuitableForVision(int width, int height)
    {
        return width > 0 && height > 0 &&
            (width < 100 || height < 100 ||
             (double)width / height > 5.0 ||
             (double)height / width > 5.0);
    }

    private static bool IsLoopingDescription(string description)
    {
        var sentences = description
            .Split([". ", "! ", "? ", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 10)
            .ToList();

        return sentences
            .GroupBy(s => s)
            .Any(g => g.Count() > 2);
    }

    // Matches the first YYYYMMDD sequence in a string (e.g. "CRW_20051124", "2005-11-24").
    private static readonly Regex DatePattern = new(
        @"(?<![0-9])(\d{4})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?![0-9])",
        RegexOptions.Compiled);

    private static bool TryParseDateFromString(string s, out DateTime result)
    {
        result = default;
        var m = DatePattern.Match(s);
        if (!m.Success) return false;
        return DateTime.TryParseExact(
            m.Value, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out result);
    }
}
