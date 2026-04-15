using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IFaceService
{
    bool IsAvailable { get; }
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Detects faces in the photo, generates embeddings, saves Face rows to the DB,
    /// writes face-crop thumbnails, and updates MediaFile.FaceDetectionStatus.
    /// Degrades silently — never throws; returns false if models are unavailable or the image fails.
    /// </summary>
    Task<bool> DetectFacesAsync(MediaFile mf, CancellationToken ct = default);
}

/// <summary>A single detected face before it is persisted.</summary>
public record DetectedFace(
    /// <summary>Bounding box as fractions of image dimensions [0,1].</summary>
    double X, double Y, double Width, double Height,
    double Confidence,
    /// <summary>512-dim ArcFace embedding, unit-normalised.</summary>
    float[] Embedding,
    /// <summary>Absolute path to the cropped face thumbnail JPEG, or null if saving failed.</summary>
    string? ThumbnailPath);
