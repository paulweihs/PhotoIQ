namespace PhotoIQPro.Core.Models;

public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public TagCategory Category { get; set; } = TagCategory.General;
    public bool IsAIGenerated { get; set; } = false;
    public double? Confidence { get; set; }
    public string? Color { get; set; }
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
}

public enum TagCategory { General, Object, Scene, Activity, Color, Style, Emotion, Text, Location, Event, Person, Custom }
