using PhotoIQPro.AI.Engines;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Vision;

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
        if (!IsAvailable) return false;

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
            var detected = await _engine.DetectAsync(imagePath, AppSettings.FacesPath);

            // Batch 1: Create all Face objects
            var faces = new List<Face>();
            foreach (var d in detected)
            {
                faces.Add(new Face
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
                });
            }

            // Batch 2: Persist all faces in a single database operation
            if (faces.Count > 0)
            {
                await _repo.BatchAddFacesAsync(faces);

                // Batch 3: Identify all faces in parallel (not sequential per-face)
                var identifyTasks = faces.Select(face => _recognition.IdentifyFaceAsync(face, ct));
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
}
