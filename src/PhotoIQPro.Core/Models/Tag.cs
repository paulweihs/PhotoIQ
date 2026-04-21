namespace PhotoIQPro.Core.Models;

/// <summary>
/// A keyword or label applied to photos for organization and search.
/// </summary>
/// <remarks>
/// Tags can be AI-generated (from vision analysis) or user-created.
/// AI tags include a confidence score; user tags do not.
/// NormalizedName enables case-insensitive tag deduplication and search.
/// Tags are many-to-many with MediaFiles and are indexed for full-text search.
/// </remarks>
public class Tag
{
    /// <summary>Unique identifier for this tag.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name of the tag (e.g. "Mountain", "Face", "Sunset").</summary>
    public required string Name { get; set; }

    /// <summary>Lowercase normalized form of Name for case-insensitive deduplication and search (e.g. "mountain").</summary>
    public required string NormalizedName { get; set; }

    /// <summary>Semantic category of this tag (General, Object, Scene, Emotion, etc.). Helps UI filtering and organization.</summary>
    public TagCategory Category { get; set; } = TagCategory.General;

    /// <summary>True if this tag was AI-generated (from vision analysis), false if user-created.</summary>
    public bool IsAIGenerated { get; set; } = false;

    /// <summary>Confidence score if AI-generated (0–1, higher = more confident). Null for user-created tags.</summary>
    public double? Confidence { get; set; }

    /// <summary>Optional hex color code (e.g. "#FF5733") for UI display (not yet implemented in v1).</summary>
    public string? Color { get; set; }

    /// <summary>UTC timestamp when this tag was created or first applied to a photo.</summary>
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property: all photos tagged with this tag (many-to-many relationship).</summary>
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
}

/// <summary>Semantic categories for tags, used to organize and filter tag display in the UI.</summary>
public enum TagCategory
{
    /// <summary>Generic tag with no specific category (default).</summary>
    General,
    /// <summary>Physical object in the photo (e.g. "Dog", "Car", "Tree").</summary>
    Object,
    /// <summary>Location or environmental setting (e.g. "Beach", "Mountain", "Office").</summary>
    Scene,
    /// <summary>Activity or action (e.g. "Running", "Playing", "Jumping").</summary>
    Activity,
    /// <summary>Dominant color (e.g. "Blue", "Red", "Golden").</summary>
    Color,
    /// <summary>Artistic or aesthetic style (e.g. "Portrait", "Landscape", "Macro").</summary>
    Style,
    /// <summary>Mood or emotion conveyed (e.g. "Happy", "Peaceful", "Dramatic").</summary>
    Emotion,
    /// <summary>Text or writing visible in the photo.</summary>
    Text,
    /// <summary>Geographic location (e.g. "Paris", "Lake Tahoe", "Yosemite").</summary>
    Location,
    /// <summary>Event or occasion (e.g. "Wedding", "Birthday", "Vacation").</summary>
    Event,
    /// <summary>Person or group of people in the photo (e.g. "Family", "Selfie", "Group").</summary>
    Person,
    /// <summary>User-created custom tag (not AI-generated).</summary>
    Custom
}
