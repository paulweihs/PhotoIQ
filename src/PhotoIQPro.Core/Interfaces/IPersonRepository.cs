using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

/// <summary>Lightweight projection used by the People browser to avoid N+1 face queries.</summary>
public record PersonSummary(
    Person  Person,
    string? KeyFaceThumbnailPath,
    int     PhotoCount);

public interface IPersonRepository
{
    Task<Person> CreateAsync(Person person);
    Task UpdateAsync(Person person);
    Task<Person?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<Person>> GetAllAsync();
    Task<IReadOnlyList<Person>> GetAllNamedAsync();

    /// <summary>
    /// Returns (PersonId, AverageEmbedding) for every Person that has an embedding.
    /// Used by recognition to find the best cosine-similarity match without loading
    /// full entities. Lightweight — embedding bytes only.
    /// </summary>
    Task<IReadOnlyList<(Guid PersonId, byte[] Embedding)>> GetAllEmbeddingsAsync();

    /// <summary>Links a Face to a Person and records the identification confidence.</summary>
    Task LinkFaceToPersonAsync(Guid faceId, Guid personId, double confidence);

    /// <summary>Batch links multiple faces to the same person with the same confidence score in a single operation.</summary>
    Task BatchLinkFacesToPersonAsync(IEnumerable<Guid> faceIds, Guid personId, double confidence);

    /// <summary>
    /// Finds an existing named Person whose NormalizedName matches <paramref name="normalizedName"/>,
    /// or creates a new named Person. Returns the Person.
    /// </summary>
    Task<Person> FindOrCreateByNameAsync(string name, string normalizedName);

    /// <summary>
    /// Returns all visible persons with their key-face thumbnail path and distinct photo count,
    /// in a single query. Named persons come first, then unnamed sorted by photo count descending.
    /// </summary>
    Task<IReadOnlyList<PersonSummary>> GetAllSummariesAsync();

    /// <summary>
    /// Returns the number of distinct photos that contain at least one face belonging to
    /// <paramref name="personId"/>.
    /// </summary>
    Task<int> GetPhotoCountAsync(Guid personId);

    /// <summary>
    /// Re-assigns all faces from <paramref name="sourceId"/> to <paramref name="targetId"/>,
    /// then deletes the source Person row and invalidates the recognition cache.
    /// </summary>
    Task MergeIntoAsync(Guid sourceId, Guid targetId);

    /// <summary>
    /// Sets IsVisible = false on the person so they are hidden from all People browser views.
    /// Faces remain linked; the person can be un-hidden in future.
    /// </summary>
    Task HideAsync(Guid personId);
}
