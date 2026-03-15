namespace PhotoIQPro.Core.Models;

public class Collection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
    public virtual Collection? Parent { get; set; }
    public CollectionType Type { get; set; } = CollectionType.Album;
    public Guid? CoverMediaFileId { get; set; }
    public int SortOrder { get; set; } = 0;
    public string? SmartCriteria { get; set; }
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime DateModified { get; set; } = DateTime.UtcNow;
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
    public virtual ICollection<Collection> Children { get; set; } = new List<Collection>();
}

public enum CollectionType { Album, Folder, Smart, Quick, Favorites, Trash }
