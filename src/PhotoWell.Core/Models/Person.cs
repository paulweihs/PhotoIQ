namespace PhotoWell.Core.Models;

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
    /// <summary>Number of face embeddings folded into AverageEmbedding. Used to compute the incremental average.</summary>
    public int FaceCount { get; set; } = 0;
    /// <summary>When true, the "Is this [name]?" review dialog is suppressed for this person — the user has chosen not to be asked again.</summary>
    public bool SuppressPrompt { get; set; } = false;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime DateModified { get; set; } = DateTime.UtcNow;
    public virtual ICollection<Face> Faces { get; set; } = new List<Face>();
}
