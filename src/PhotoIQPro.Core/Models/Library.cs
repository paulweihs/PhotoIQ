namespace PhotoIQPro.Core.Models;

public class Library
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public virtual ICollection<Album> Albums { get; set; } = new List<Album>();
}

public class Album
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LibraryId { get; set; }
    public virtual Library Library { get; set; } = null!;
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? CoverPhotoId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
}
