namespace PhotoWell.Core.Models;

/// <summary>
/// A detected face in a photo, with bounding box, embedding, and optional person identity.
/// </summary>
/// <remarks>
/// Faces are detected by the UltraFace model during FaceService.DetectFacesAsync.
/// Each face includes an ArcFace embedding (normalized 512-d vector) for recognition.
/// The FaceRecognitionService matches embeddings against known persons and sets PersonId/IdentificationConfidence.
/// Users can manually verify or correct face-to-person mappings in PhotoViewerViewModel.
/// </remarks>
public class Face
{
    /// <summary>Unique identifier for this face detection.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The ID of the photo containing this face.</summary>
    public Guid MediaFileId { get; set; }

    /// <summary>Navigation property: the photo containing this face.</summary>
    public virtual MediaFile? MediaFile { get; set; }

    /// <summary>Optional: the person this face has been identified as. Null if unidentified.</summary>
    public Guid? PersonId { get; set; }

    /// <summary>Navigation property: the identified person (if PersonId is set).</summary>
    public virtual Person? Person { get; set; }

    /// <summary>Bounding box X coordinate (normalized to photo width, 0–1).</summary>
    public double X { get; set; }

    /// <summary>Bounding box Y coordinate (normalized to photo height, 0–1).</summary>
    public double Y { get; set; }

    /// <summary>Bounding box width (normalized to photo width, 0–1).</summary>
    public double Width { get; set; }

    /// <summary>Bounding box height (normalized to photo height, 0–1).</summary>
    public double Height { get; set; }

    /// <summary>
    /// ArcFace embedding as a byte array (512-dimensional float vector, 2048 bytes total).
    /// Used for face recognition via cosine similarity matching against known persons.
    /// </summary>
    public byte[]? Embedding { get; set; }

    /// <summary>Confidence score from UltraFace detection (0–1). Higher = more confident this region contains a face.</summary>
    public double DetectionConfidence { get; set; }

    /// <summary>Confidence score from ArcFace identification (0–1). Null if unidentified. Higher = more confident the match is correct.</summary>
    public double? IdentificationConfidence { get; set; }

    /// <summary>Optional path to a cropped face thumbnail image (used in PhotoViewerViewModel UI).</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>UTC timestamp when this face was detected.</summary>
    public DateTime DateDetected { get; set; } = DateTime.UtcNow;

    /// <summary>True when the user has manually confirmed or assigned this face-to-person link.
    /// Confirmed faces are preserved (by bounding-box overlap) when faces are re-detected during re-analysis.</summary>
    public bool IsUserConfirmed { get; set; } = false;

    /// <summary>True when this record is a manual person tag (user clicked empty image area) rather than an
    /// AI-detected face. IsManualMention faces have no bounding box, embedding, or thumbnail and are
    /// never deleted by re-detection — they are purely for indexing "person is in this photo".</summary>
    public bool IsManualMention { get; set; } = false;
}
