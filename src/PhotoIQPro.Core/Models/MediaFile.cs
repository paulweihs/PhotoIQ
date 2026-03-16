namespace PhotoIQPro.Core.Models;

public class MediaFile
{
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
    public string? Caption { get; set; }
    public string? AiDescription { get; set; }
    public bool IsAnalyzed { get; set; } = false;
    public DateTime? DateAnalyzed { get; set; }
    public string? ThumbnailSmall { get; set; }
    public string? ThumbnailMedium { get; set; }
    public string? ThumbnailLarge { get; set; }
    public MediaType MediaType { get; set; } = MediaType.Photo;
    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public virtual ICollection<Face> Faces { get; set; } = new List<Face>();
    public virtual ICollection<Collection> Collections { get; set; } = new List<Collection>();
}

public enum MediaType { Photo, Video, Raw }
