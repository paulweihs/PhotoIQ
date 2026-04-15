namespace PhotoIQPro.Core.Models;

public class Face
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaFileId { get; set; }
    public virtual MediaFile? MediaFile { get; set; }
    public Guid? PersonId { get; set; }
    public virtual Person? Person { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public byte[]? Embedding { get; set; }
    public double DetectionConfidence { get; set; }
    public double? IdentificationConfidence { get; set; }
    public string? ThumbnailPath { get; set; }
    public DateTime DateDetected { get; set; } = DateTime.UtcNow;
}
