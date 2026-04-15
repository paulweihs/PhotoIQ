using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Runtime.CompilerServices;

namespace PhotoIQPro.Core.Models;

public class MediaFile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    // Always raises — no equality guard — so re-assigning the same path still triggers binding refresh.
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public required string Extension { get; set; }
    public long FileSize { get; set; }
    public string? FileHash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime? DateTaken { get; set; }
    public DateTime DateImported { get; set; } = DateTime.UtcNow;
    public DateTime DateModified { get; set; } = DateTime.UtcNow;
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? LensModel { get; set; }
    public double? FocalLength { get; set; }
    public double? Aperture { get; set; }
    public string? ShutterSpeed { get; set; }
    public int? ISO { get; set; }
    public bool? Flash { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int Rating { get; set; } = 0;
    public bool IsFavorite { get; set; } = false;
    public bool IsRejected { get; set; } = false;
    public bool IsExcluded { get; set; } = false;
    /// <summary>ADR-008: User-authored caption (short title). Maps to legacy "Caption" DB column. NULL = never set.</summary>
    [Column("Caption")]
    public string? UserCaption { get; set; }
    /// <summary>ADR-008: Raw caption text as the user typed it, before any normalization. NULL = never set.</summary>
    public string? UserCaptionRaw { get; set; }
    /// <summary>ADR-008: When the user last edited the caption. NULL = never edited.</summary>
    public DateTime? CaptionEditedAt { get; set; }
    public string? AiDescription { get; set; }
    /// <summary>Name of the vision model that generated AiDescription (e.g. "llama3.2-vision"). Null if no description was produced.</summary>
    public string? AiModelUsed { get; set; }
    /// <summary>Prompt version used when AiDescription was generated (e.g. "v10a"). Null for photos analyzed before version tracking was added.</summary>
    public string? PromptVersion { get; set; }
    /// <summary>User-edited description. NULL = never edited. Takes display priority over AiDescription.</summary>
    public string? UserDescription { get; set; }
    public DateTime? DescriptionEditedAt { get; set; }
    public bool IsAnalyzed { get; set; } = false;
    public DateTime? DateAnalyzed { get; set; }
    private string? _thumbnailSmall;
    private string? _thumbnailMedium;
    private string? _thumbnailLarge;
    public string? ThumbnailSmall  { get => _thumbnailSmall;  set { _thumbnailSmall  = value; Notify(); } }
    public string? ThumbnailMedium { get => _thumbnailMedium; set { _thumbnailMedium = value; Notify(); } }
    public string? ThumbnailLarge  { get => _thumbnailLarge;  set { _thumbnailLarge  = value; Notify(); } }
    public MediaType MediaType { get; set; } = MediaType.Photo;
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.Pending;
    public FaceDetectionStatus FaceDetectionStatus { get; set; } = FaceDetectionStatus.Pending;
    /// <summary>True once the user has confirmed or dismissed the "who is in this photo?" prompt for this photo.</summary>
    public bool FacesReviewed { get; set; } = false;
    public byte[]? ClipEmbeddingBytes { get; set; }

    /// <summary>Set at load time — true when the file's source drive is not accessible. Not persisted.</summary>
    private bool _isOffline;
    [NotMapped]
    public bool IsOffline
    {
        get => _isOffline;
        set { if (_isOffline == value) return; _isOffline = value; Notify(); }
    }

    /// <summary>Pre-loaded thumbnail bitmap (BitmapSource). Null until background loader sets it. Not persisted.</summary>
    private object? _loadedThumbnail;
    [NotMapped]
    public object? LoadedThumbnail
    {
        get => _loadedThumbnail;
        set { if (ReferenceEquals(_loadedThumbnail, value)) return; _loadedThumbnail = value; Notify(); }
    }

    /// <summary>True when CLIP ran but Ollama vision hasn't yet processed this photo. Not persisted (derived from AnalysisStatus).</summary>
    [NotMapped]
    public bool IsVisionPending => AnalysisStatus == AnalysisStatus.VisionPending;

    /// <summary>Drive root (e.g. "E:") used in offline tooltip. Not persisted.</summary>
    [NotMapped]
    public string OfflineDriveRoot => Path.GetPathRoot(FilePath)?.TrimEnd('\\', '/') ?? "";
    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public virtual ICollection<Face> Faces { get; set; } = new List<Face>();
    public virtual ICollection<Album> Albums { get; set; } = new List<Album>();
}

public enum MediaType { Photo, Video, Raw }

public enum FaceDetectionStatus
{
    Pending  = 0, // not yet attempted
    Complete = 1, // detection ran; zero or more faces stored
    Skipped  = 2, // unsuitable (video, too small, unsupported type)
}

public enum AnalysisStatus
{
    Pending       = 0, // not yet attempted
    Processing    = 1, // in-flight — if seen at startup, crashed mid-analysis → reset to Pending
    Complete      = 2, // CLIP + vision both ran (description may still be empty)
    Failed        = 3, // unhandled exception prevented analysis from running
    VisionPending = 4, // CLIP done; waiting for background vision (Ollama) pass
}
