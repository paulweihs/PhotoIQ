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

    public bool IsAvailable => _engine.IsInitialized;

    public async Task InitializeAsync(CancellationToken ct = default)
        => await _engine.InitializeAsync();

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

                await _repo.AddFaceAsync(face);
                await _recognition.IdentifyFaceAsync(face, ct);
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
