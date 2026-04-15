using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using PhotoIQPro.Common;
using static PhotoIQPro.Common.SupportedFormats;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Import;

public class ImportService : IImportService
{
    private readonly IMediaFileRepository       _repo;
    private readonly IThumbnailService          _thumbs;
    private readonly ITaggingService            _tagging;
    private readonly IImageUnderstandingService _vision;
    private readonly IImagePreprocessor         _preprocessor;
    private readonly IAnalysisMetricRepository  _metrics;
    private readonly IFaceService               _faces;

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

    public bool IsSupportedFile(string path) =>
        AllExtensions.Contains(Path.GetExtension(path));

    public async Task<MediaFile?> ImportFileAsync(string filePath, CancellationToken ct = default)
    {
        var exists = await _repo.ExistsAsync(filePath);
        if (!File.Exists(filePath) || !IsSupportedFile(filePath) || exists) return null;

        var fi  = new FileInfo(filePath);
        var ext = fi.Extension.ToLowerInvariant();
        var mf  = new MediaFile
        {
            FilePath  = filePath,
            FileName  = fi.Name,
            Extension = ext,
            FileSize  = fi.Length,
            MediaType = RawExtensions.Contains(ext) ? MediaType.Raw : MediaType.Photo
        };

        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(filePath))
            mf.FileHash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));

        ExtractMetadata(mf, filePath);

        await _repo.AddAsync(mf);
        await _thumbs.GenerateThumbnailsAsync(mf, ct);
        await _faces.DetectFacesAsync(mf, ct);
        await RunAnalysisAsync(mf, ct, clipOnly: true);
        await _repo.UpdateAsync(mf);
        return mf;
    }

    // ── Re-analyze ────────────────────────────────────────────────────────────

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

        foreach (var t in mf.Tags.Where(t => t.IsAIGenerated).ToList())
            mf.Tags.Remove(t);
        mf.AiDescription   = null;
        mf.AiModelUsed     = null;
        mf.PromptVersion   = null;
        mf.IsAnalyzed      = false;
        mf.AnalysisStatus  = AnalysisStatus.Pending;
        await _repo.UpdateAsync(mf);

        // Reload fresh so EF Core's change tracker has no stale state from the tag strip.
        // Without this, re-adding the same Tag entities causes silent SaveChanges conflicts.
        mf = (await _repo.GetByIdAsync(mediaFileId))!;
        await RunAnalysisAsync(mf, ct);
        await _repo.UpdateAsync(mf);

        progress?.Report(new ImportProgress(1, 1, mf.IsAnalyzed ? 1 : 0, 0, mf.IsAnalyzed ? 0 : 1, mf.FilePath));
        return mf.IsAnalyzed;
    }

    public async Task<ImportResult> ReanalyzeAllAsync(
        bool unanalyzedOnly,
        bool skipUserModified = false,
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onPhotoAnalyzed = null,
        CancellationToken ct = default)
    {
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        // Tags are loaded upfront to avoid a GetByIdAsync per photo just to strip AI tags.
        var all    = await _repo.GetAllWithTagsAsync();
        var photos = (unanalyzedOnly ? all.Where(m => m.AnalysisStatus != AnalysisStatus.Complete) : all)
                     .Where(m => !skipUserModified || m.UserDescription == null)
                     .ToList();

        const int BatchSize = 5;
        int total = photos.Count, processed = 0, reanalyzed = 0, skipped = 0, failed = 0;
        var batchBuffer = new List<MediaFile>(BatchSize);

        async Task FlushBatchAsync()
        {
            if (batchBuffer.Count == 0) return;
            await _repo.BatchUpdateAsync(batchBuffer);
            foreach (var m in batchBuffer)
                onPhotoAnalyzed?.Invoke(m);
            batchBuffer.Clear();
        }

        // Kick off preprocessing for the first photo immediately.
        // For each photo the lookahead task runs PreprocessFileAsync (file I/O only, no DB)
        // while Ollama is doing inference on the current photo, hiding the conversion latency.
        Task<(string, PreparedImage?)>? lookaheadTask =
            photos.Count > 0 ? PreprocessFileAsync(photos[0], ct) : null;

        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var photo = photos[i];

            // ── DB: clear AI state, reload fresh ───────────────────────────
            // Tags were loaded upfront; no extra GetByIdAsync needed to strip them.
            var mf = photo;
            foreach (var t in mf.Tags.Where(t => t.IsAIGenerated).ToList()) mf.Tags.Remove(t);
            mf.AiDescription  = null;
            mf.AiModelUsed    = null;
            mf.PromptVersion  = null;
            mf.IsAnalyzed     = false;
            mf.AnalysisStatus = AnalysisStatus.Pending;
            await _repo.UpdateAsync(mf);
            // Reload so EF's change tracker has no stale state from the tag strip —
            // re-adding the same Tag entities after RunAnalysisAsync would otherwise conflict.
            mf = (await _repo.GetByIdAsync(mf.Id))!;

            // ── Collect lookahead result for this photo ─────────────────────
            var (analysisPath, prepared) = lookaheadTask != null
                ? await lookaheadTask
                : await PreprocessFileAsync(mf, ct);

            // ── Start lookahead for next photo (pure file I/O, no EF ops) ──
            lookaheadTask = i + 1 < photos.Count
                ? PreprocessFileAsync(photos[i + 1], ct)
                : null;

            // ── Run inference ───────────────────────────────────────────────
            if (!File.Exists(mf.FilePath))
            {
                var root = Path.GetPathRoot(mf.FilePath);
                bool driveOffline = !string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root);
                AppLog.Vision(driveOffline
                    ? $"Skipped {mf.FileName} — drive offline ({root})"
                    : $"Skipped {mf.FileName} — file not found");
                // Leave AnalysisStatus = Pending so it is retried when the drive reconnects.
                prepared?.Dispose();
                skipped++;
            }
            else
            {
                try
                {
                    await RunAnalysisAsync(mf, ct, analysisPath, prepared);
                    if (mf.IsAnalyzed) reanalyzed++; else skipped++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{mf.FileName}: {ex.Message}");
                    // Reset to Pending so the heal worker can retry on next startup.
                    // Setting Complete here would falsely mark it as analyzed and prevent retries.
                    mf.AnalysisStatus = AnalysisStatus.Pending;
                    AppLog.Error($"Analysis failed for '{mf.FilePath}': {ex.GetType().Name}: {ex.Message}");
                }
                finally { prepared?.Dispose(); }

                batchBuffer.Add(mf);
                if (batchBuffer.Count >= BatchSize)
                    await FlushBatchAsync();
            }

            processed++;
            progress?.Report(new ImportProgress(total, processed, reanalyzed, skipped, failed, mf.FilePath));
        }

        await FlushBatchAsync();
        return new ImportResult(total, reanalyzed, skipped, failed, DateTime.UtcNow - start, errors);
    }

    public async Task<ImportResult> ReanalyzeOutdatedAsync(
        bool skipUserModified = false,
        IProgress<ImportProgress>? progress = null,
        Action<MediaFile>? onPhotoAnalyzed = null,
        CancellationToken ct = default)
    {
        var photos = (await _repo.GetOutdatedDescriptionsAsync(
            UserPreferences.Current.VisionModelName,
            _vision.PromptVersion))
            .Where(m => !skipUserModified || m.UserDescription == null)
            .ToList();

        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        const int BatchSize = 5;
        int total = photos.Count, processed = 0, reanalyzed = 0, skipped = 0, failed = 0;
        var batchBuffer = new List<MediaFile>(BatchSize);

        async Task FlushBatchAsync()
        {
            if (batchBuffer.Count == 0) return;
            await _repo.BatchUpdateAsync(batchBuffer);
            foreach (var m in batchBuffer) onPhotoAnalyzed?.Invoke(m);
            batchBuffer.Clear();
        }

        Task<(string, PreparedImage?)>? lookaheadTask =
            photos.Count > 0 ? PreprocessFileAsync(photos[0], ct) : null;

        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var photo = photos[i];

            // Tags were loaded upfront via GetOutdatedDescriptionsAsync; no extra GetByIdAsync needed.
            var mf = photo;
            foreach (var t in mf.Tags.Where(t => t.IsAIGenerated).ToList()) mf.Tags.Remove(t);
            mf.AiDescription  = null;
            mf.AiModelUsed    = null;
            mf.PromptVersion  = null;
            mf.IsAnalyzed     = false;
            mf.AnalysisStatus = AnalysisStatus.Pending;
            await _repo.UpdateAsync(mf);
            // Reload so EF's change tracker has no stale state from the tag strip.
            mf = (await _repo.GetByIdAsync(mf.Id))!;

            var (analysisPath, prepared) = lookaheadTask != null
                ? await lookaheadTask
                : await PreprocessFileAsync(mf, ct);

            lookaheadTask = i + 1 < photos.Count
                ? PreprocessFileAsync(photos[i + 1], ct)
                : null;

            if (!File.Exists(mf.FilePath))
            {
                prepared?.Dispose();
                skipped++;
            }
            else
            {
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
                if (batchBuffer.Count >= BatchSize)
                    await FlushBatchAsync();
            }

            processed++;
            progress?.Report(new ImportProgress(total, processed, reanalyzed, skipped, failed, mf.FilePath));
        }

        await FlushBatchAsync();
        return new ImportResult(total, reanalyzed, skipped, failed, DateTime.UtcNow - start, errors);
    }

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

        // Dimension gate — logos, banners, and icons produce hallucinated descriptions.
        if (mf.Width > 0 && mf.Height > 0 &&
            (mf.Width < 100 || mf.Height < 100 ||
             (double)mf.Width / mf.Height > 5.0 ||
             (double)mf.Height / mf.Width > 5.0))
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
                    mf.AiModelUsed   = UserPreferences.Current.VisionModelName;
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

        mf.AnalysisStatus = AnalysisStatus.Complete;
        await _repo.UpdateAsync(mf);
    }

    public async Task<ImportResult> RegenerateThumbnailsAsync(IProgress<ImportProgress>? progress = null, Action<MediaFile>? onThumbnailRegenerated = null, CancellationToken ct = default)
    {
        var start  = DateTime.UtcNow;
        var errors = new List<string>();
        var all    = (await _repo.GetAllAsync()).ToList();
        int total = all.Count, processed = 0, updated = 0, skipped = 0, failed = 0;
        foreach (var mf in all)
        {
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
        return new ImportResult(total, updated, skipped, failed, DateTime.UtcNow - start, errors);
    }

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

            // CLIP tagging — use preprocessed path for RAW so CLIP sees a real image
            try
            {
                var predictions = await _tagging.GenerateTagsAsync(analysisPath, ct);
                AppLog.Vision($"CLIP: {predictions.Count} predictions for {mf.FileName}");
                foreach (var p in predictions)
                {
                    var tag = await _repo.GetOrCreateTagAsync(p.Label, p.Label.ToLowerInvariant(), p.Category, true, p.Confidence);
                    if (!mf.Tags.Contains(tag)) mf.Tags.Add(tag);
                }
                if (predictions.Count > 0) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }

                // Date-based holiday tags — applied regardless of visual content.
                if (mf.DateTaken.HasValue)
                {
                    var holidayName = mf.DateTaken.Value.GetHolidayName();
                    if (holidayName != null)
                    {
                        var tag = await _repo.GetOrCreateTagAsync(holidayName, holidayName, TagCategory.Activity, true, 1.0f);
                        if (!mf.Tags.Contains(tag)) mf.Tags.Add(tag);
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

            // Vision description (llama3.2-vision via Ollama) — photos and RAW files; skipped on import (clipOnly) for live gallery
            if (!clipOnly && (mf.MediaType == MediaType.Photo || mf.MediaType == MediaType.Raw))
            {
                PreparedImage? ollamaPrepared = null;
                try
                {
                    string? visionPath;
                    // Prefer the pre-generated large thumbnail (800px JPEG) — already on disk,
                    // smaller I/O, no temp-file creation needed for any format including RAW.
                    if (!string.IsNullOrEmpty(mf.ThumbnailLarge) && File.Exists(mf.ThumbnailLarge))
                    {
                        visionPath = mf.ThumbnailLarge;
                    }
                    else if (prepared != null)
                    {
                        // Reuse the already-prepared image from CLIP preprocessing or lookahead.
                        visionPath = prepared.Path;
                    }
                    else if (needsPreprocess)
                    {
                        // Preprocessing was needed but failed — skip vision.
                        AppLog.Vision($"SKIP vision for {mf.FileName} — preprocessor returned null");
                        mf.AiDescription = string.Empty;
                        visionPath = null;
                    }
                    else
                    {
                        // Native JPEG — no thumbnail yet; prepare for Ollama.
                        AppLog.Vision($"Preprocessing {mf.FileName} for vision...");
                        var swPre2 = System.Diagnostics.Stopwatch.StartNew();
                        ollamaPrepared  = await _preprocessor.PrepareAsync(mf.FilePath, ct);
                        preprocessingMs += swPre2.ElapsedMilliseconds;
                        visionPath = ollamaPrepared?.Path ?? mf.FilePath;
                    }

                    if (visionPath != null)
                    {
                        // Dimension gate — logos, banners, and icons produce hallucinated descriptions
                        // because the vision model receives a heavily distorted input after rescaling.
                        // Skip when either dimension is < 100px OR aspect ratio exceeds 5:1.
                        bool unsuitableForVision = mf.Width > 0 && mf.Height > 0 &&
                            (mf.Width < 100 || mf.Height < 100 ||
                             (double)mf.Width / mf.Height > 5.0 ||
                             (double)mf.Height / mf.Width > 5.0);
                        if (unsuitableForVision)
                        {
                            AppLog.Vision($"Skipped vision for {mf.FileName} — unsuitable dimensions {mf.Width}×{mf.Height}");
                            mf.AiDescription = string.Empty;
                            visionPath = null; // skip the analysis below
                        }
                    }
                    if (visionPath != null)
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
                            }
                            else
                            {
                                mf.AiDescription = understanding.Description;
                                mf.AiModelUsed   = UserPreferences.Current.VisionModelName;
                                mf.PromptVersion = _vision.PromptVersion;
                                gpuUsed = true;
                                if (!mf.IsAnalyzed) { mf.IsAnalyzed = true; mf.DateAnalyzed = DateTime.UtcNow; }
                            }
                        }
                        else
                        {
                            AppLog.Vision($"Vision returned empty description for {mf.FileName}");
                            mf.AiDescription = string.Empty;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Vision($"EXCEPTION in vision pipeline for {mf.FileName} — {ex.GetType().Name}: {ex.Message}");
                    mf.AiDescription = string.Empty;
                }
                finally { ollamaPrepared?.Dispose(); }
            }

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
