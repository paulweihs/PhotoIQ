namespace PhotoIQPro.Core.Models;

public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public bool IsNamed { get; set; } = false;
    public bool IsVisible { get; set; } = true;
    public bool IsFavorite { get; set; } = false;
    public Guid? KeyFaceId { get; set; }
    public byte[]? AverageEmbedding { get; set; }
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime DateModified { get; set; } = DateTime.UtcNow;
    public virtual ICollection<Face> Faces { get; set; } = new List<Face>();
}
