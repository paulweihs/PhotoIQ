namespace PhotoWell.Core.Models;

/// <summary>
/// A user-created library — a top-level container for organizing photos into albums.
/// </summary>
/// <remarks>
/// Users can create multiple libraries (e.g. "Family", "Travel", "Archive") to logically separate their photo collections.
/// Each library contains multiple albums, which are the actual groupings of photos.
/// Libraries are managed via MainViewModel.CreateLibraryAsync / RenameLibraryAsync / DeleteLibraryAsync.
/// </remarks>
public class Library
{
    /// <summary>Unique identifier for this library.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name of the library (required, user-editable).</summary>
    public required string Name { get; set; }

    /// <summary>Optional user-provided description of the library's purpose or contents.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp when this library was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property: all albums in this library.</summary>
    public virtual ICollection<Album> Albums { get; set; } = new List<Album>();
}

/// <summary>
/// A photo collection within a library — groups related photos together.
/// </summary>
/// <remarks>
/// Albums are the primary way users organize photos within a library (e.g. "Summer 2024", "Vacation", "Pets").
/// Each album can have a cover photo. Albums are managed via MainViewModel and LibraryRepository.
/// Photos can belong to multiple albums (many-to-many relationship).
/// </remarks>
public class Album
{
    /// <summary>Unique identifier for this album.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The ID of the library containing this album.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Navigation property: the parent library.</summary>
    public virtual Library Library { get; set; } = null!;

    /// <summary>Display name of the album (required, user-editable).</summary>
    public required string Name { get; set; }

    /// <summary>Optional user-provided description of the album's theme or contents.</summary>
    public string? Description { get; set; }

    /// <summary>Optional ID of a photo to use as the album's cover thumbnail in the UI.</summary>
    public Guid? CoverPhotoId { get; set; }

    /// <summary>UTC timestamp when this album was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property: all photos in this album (many-to-many via photo_albums junction table).</summary>
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
}
