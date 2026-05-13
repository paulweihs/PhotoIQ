using PhotoWell.AI.Engines;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Services.Vision;

/// <summary>
/// Orchestrates the full face pipeline for a single photo:
///   1. Run FaceDetectionEngine (UltraFace detection + ArcFace embedding)
///   2. Persist Face rows to the DB
///   3. Run FaceRecognitionService to match/cluster each face against known persons
///   4. Update MediaFile.FaceDetectionStatus
///
/// Registered as Scoped so it can take IMediaFileRepository directly.
/// FaceDetectionEngine and IFaceRecognitionService are Singletons injected safely.
/// </summary>
public sealed class FaceService : IFaceService
{
    private readonly FaceDetectionEngine    _engine;
    private readonly IMediaFileRepository   _repo;
    private readonly IFaceRecognitionService _recognition;

    public FaceService(
        FaceDetectionEngine     engine,
        IMediaFileRepository    repo,
        IFaceRecognitionService recognition)
    {
        _engine      = engine;
        _repo        = repo;
        _recognition = recognition;
    }

    /// <summary>Gets a value indicating whether face detection is available (engine initialized).</summary>
    public bool IsAvailable => _engine.IsInitialized;

    /// <summary>Initializes the face detection engine (loads UltraFace and ArcFace models).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
        => await _engine.InitializeAsync();

    /// <summary>
    /// Detects and recognizes faces in a photo, persisting results to the database.
    /// </summary>
    /// <remarks>
    /// Skips videos and very small images (&lt;80×80). Prefers thumbnail over original for speed.
    /// Returns false if face detection is unavailable or the file is not found.
    /// </remarks>
    /// <param name="mf">The MediaFile to analyze (Width/Height and FilePath must be populated).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if faces were detected and persisted; false if skipped or unavailable.</returns>
    public async Task<bool> DetectFacesAsync(MediaFile mf, CancellationToken ct = default)
    {
        AppLog.Info($"FaceService.DetectFacesAsync called for {mf.FileName}. IsAvailable={IsAvailable}");
        if (!IsAvailable)
        {
            AppLog.Info($"Face detection not available for {mf.FileName}");
            return false;
        }

        if (mf.MediaType == MediaType.Video)
        {
            mf.FaceDetectionStatus = FaceDetectionStatus.Skipped;
            return false;
        }

        if (mf.Width > 0 && mf.Height > 0 && (mf.Width < 80 || mf.Height < 80))
        {
            mf.FaceDetectionStatus = FaceDetectionStatus.Skipped;
            return false;
        }

        var imagePath = !string.IsNullOrEmpty(mf.ThumbnailLarge) && File.Exists(mf.ThumbnailLarge)
            ? mf.ThumbnailLarge
            : mf.FilePath;

        if (!File.Exists(imagePath)) return false;

        try
        {
            // Save confirmed assignments before deletion so we can restore them after re-detection.
            var confirmed = await _repo.GetConfirmedFacesForPhotoAsync(mf.Id);

            // Delete old faces and re-detect fresh.
            await _repo.DeleteFacesForPhotoAsync(mf.Id);

            var detected = await _engine.DetectAsync(imagePath, AppSettings.FacesPath);

            // Batch 1: Create all Face objects, restoring confirmed assignments by bounding-box overlap.
            var faces = new List<Face>();
            foreach (var d in detected)
            {
                var face = new Face
                {
                    MediaFileId         = mf.Id,
                    X                   = d.X,
                    Y                   = d.Y,
                    Width               = d.Width,
                    Height              = d.Height,
                    DetectionConfidence = d.Confidence,
                    Embedding           = EmbeddingToBytes(d.Embedding),
                    ThumbnailPath       = d.ThumbnailPath,
                    DateDetected        = DateTime.UtcNow,
                };

                // Restore confirmed person link if a prior confirmed face overlaps this detection (IoU > 0.5).
                var match = confirmed.FirstOrDefault(c => ComputeIoU(c.X, c.Y, c.W, c.H, d.X, d.Y, d.Width, d.Height) > 0.5f);
                if (match != default)
                {
                    face.PersonId               = match.PersonId;
                    face.IdentificationConfidence = 1.0;
                    face.IsUserConfirmed        = true;
                }

                faces.Add(face);
            }

            // Batch 2: Persist all faces in a single database operation
            if (faces.Count > 0)
            {
                await _repo.BatchAddFacesAsync(faces);

                // Batch 3: Identify only faces that were not restored from confirmed assignments.
                var identifyTasks = faces
                    .Where(f => !f.IsUserConfirmed)
                    .Select(face => _recognition.IdentifyFaceAsync(face, ct));
                await Task.WhenAll(identifyTasks);
            }

            mf.FaceDetectionStatus = FaceDetectionStatus.Complete;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error($"FaceService.DetectFacesAsync failed for '{mf.FileName}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static byte[] EmbeddingToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Intersection-over-union for two normalized bounding boxes. Returns 0–1.</summary>
    private static float ComputeIoU(double ax, double ay, double aw, double ah,
                                     double bx, double by, double bw, double bh)
    {
        double ix  = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx));
        double iy  = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
        double intersection = ix * iy;
        if (intersection == 0) return 0;
        double union = aw * ah + bw * bh - intersection;
        return union > 0 ? (float)(intersection / union) : 0;
    }
}
