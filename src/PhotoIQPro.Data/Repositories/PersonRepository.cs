using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

/// <summary>
/// Repository for Person (face recognition identity) persistence and queries.
/// </summary>
/// <remarks>
/// People represent recognized identities in face detection. A Person may be:
/// - IsNamed: user has assigned a name (e.g., "Alice") or the name was imported from metadata
/// - UnNamed: auto-generated person for clustered faces not yet identified (e.g., "Person #1")
/// - Invisible: created during face merging but marked for deletion without losing face records
///
/// Each Person has an AverageEmbedding (computed from all their face embeddings) used for matching
/// new detected faces to existing identities. The repository handles CRUD + embedding lookups and
/// face linkage (assigning detected faces to Person identities with confidence scores).
/// </remarks>
public class PersonRepository : IPersonRepository
{
    private readonly PhotoIQContext _ctx;
    /// <summary>Initializes a new instance of the PersonRepository.</summary>
    /// <param name="ctx">The PhotoIQContext for database access.</param>
    public PersonRepository(PhotoIQContext ctx) => _ctx = ctx;

    /// <summary>Creates a new Person (identity) in the database.</summary>
    /// <remarks>
    /// Typically called when a new face cluster is identified or when a user manually creates a person.
    /// The caller should populate IsNamed and AverageEmbedding as appropriate.
    /// </remarks>
    /// <param name="person">The Person entity to create.</param>
    /// <returns>The created Person (with ID populated from the database).</returns>
    public async Task<Person> CreateAsync(Person person)
    {
        _ctx.People.Add(person);
        await _ctx.SaveChangesAsync();
        return person;
    }

    /// <summary>Updates an existing Person's metadata (name, embeddings, visibility, etc.).</summary>
    /// <remarks>Sets DateModified to UTC now before saving.</remarks>
    /// <param name="person">The Person entity with updated fields (must be already attached to context or have correct ID).</param>
    public async Task UpdateAsync(Person person)
    {
        person.DateModified = DateTime.UtcNow;
        _ctx.People.Update(person);
        await _ctx.SaveChangesAsync();
    }

    /// <summary>Gets a person by ID (regardless of visibility state).</summary>
    /// <param name="id">The Person ID.</param>
    /// <returns>The Person entity, or null if not found.</returns>
    public async Task<Person?> GetByIdAsync(Guid id)
        => await _ctx.People.FirstOrDefaultAsync(p => p.Id == id);

    /// <summary>Gets all visible persons (both named and unnamed), sorted by name.</summary>
    /// <remarks>Invisible persons (marked for deletion) are excluded.</remarks>
    /// <returns>All visible Person entities in name order.</returns>
    public async Task<IReadOnlyList<Person>> GetAllAsync()
        => await _ctx.People.Where(p => p.IsVisible).OrderBy(p => p.Name).ToListAsync();

    /// <summary>Gets all visible persons that have been explicitly named by the user.</summary>
    /// <remarks>Excludes unnamed persons (auto-generated clusters like "Person #1").</remarks>
    /// <returns>All named, visible Person entities in name order.</returns>
    public async Task<IReadOnlyList<Person>> GetAllNamedAsync()
        => await _ctx.People.Where(p => p.IsNamed && p.IsVisible).OrderBy(p => p.Name).ToListAsync();

    /// <summary>
    /// Gets all persons with computed average face embeddings (for face recognition matching).
    /// </summary>
    /// <remarks>
    /// Returns only persons whose AverageEmbedding is populated. These embeddings are used by the
    /// face recognition service to match newly detected faces to known identities via cosine similarity.
    /// Uses AsNoTracking for efficiency (no change tracking needed for read-only embedding data).
    /// </remarks>
    /// <returns>A list of (PersonId, AverageEmbedding) tuples for matching.</returns>
    public async Task<IReadOnlyList<(Guid PersonId, byte[] Embedding)>> GetAllEmbeddingsAsync()
    {
        var rows = await _ctx.People
            .Where(p => p.AverageEmbedding != null)
            .Select(p => new { p.Id, p.AverageEmbedding })
            .AsNoTracking()
            .ToListAsync();
        return rows.Select(r => (r.Id, r.AverageEmbedding!)).ToList();
    }

    /// <summary>Links a detected face to a person identity with a confidence score.</summary>
    /// <remarks>
    /// Called after the face recognition engine matches a face to a person. Silently succeeds
    /// even if the face is not found (idempotent).
    /// </remarks>
    /// <param name="faceId">The Face ID to link.</param>
    /// <param name="personId">The Person ID to link to.</param>
    /// <param name="confidence">Confidence score (0-1) from the face recognition model.</param>
    public async Task LinkFaceToPersonAsync(Guid faceId, Guid personId, double confidence)
    {
        var face = await _ctx.Faces.FirstOrDefaultAsync(f => f.Id == faceId);
        if (face == null) return;
        face.PersonId                  = personId;
        face.IdentificationConfidence  = confidence;
        await _ctx.SaveChangesAsync();
    }

    /// <summary>Batch links multiple faces to the same person with the same confidence score.</summary>
    /// <remarks>
    /// More efficient than calling LinkFaceToPersonAsync in a loop. Single database operation.
    /// Silently succeeds even if some faces don't exist.
    /// </remarks>
    public async Task BatchLinkFacesToPersonAsync(IEnumerable<Guid> faceIds, Guid personId, double confidence)
    {
        var ids = faceIds.ToList();
        if (ids.Count == 0) return;
        await _ctx.Faces
            .Where(f => ids.Contains(f.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.PersonId,                 personId)
                .SetProperty(f => f.IdentificationConfidence, confidence));
    }

    /// <summary>Finds a person by normalized name, or creates one if not found.</summary>
    /// <remarks>
    /// Used when importing faces with metadata tags (e.g., EXIF Person keywords) or when the user
    /// manually names a person. The normalized name ensures consistent deduplication regardless of case.
    /// </remarks>
    /// <param name="name">The display name (e.g., "Alice").</param>
    /// <param name="normalizedName">The normalized name for deduplication (e.g., "alice").</param>
    /// <returns>The existing Person with this normalized name, or a newly created one.</returns>
    public async Task<Person> FindOrCreateByNameAsync(string name, string normalizedName)
    {
        var existing = await _ctx.People
            .FirstOrDefaultAsync(p => p.NormalizedName == normalizedName && p.IsNamed);
        if (existing != null) return existing;

        var person = new Person
        {
            Name           = name,
            NormalizedName = normalizedName,
            IsNamed        = true,
        };
        _ctx.People.Add(person);
        await _ctx.SaveChangesAsync();
        return person;
    }

    public async Task<IReadOnlyList<PersonSummary>> GetAllSummariesAsync()
    {
        var persons = await _ctx.People
            .Where(p => p.IsVisible)
            .AsNoTracking()
            .ToListAsync();

        if (persons.Count == 0) return [];

        // Batch-fetch key-face thumbnail paths in one query.
        var keyFaceIds = persons
            .Where(p => p.KeyFaceId.HasValue)
            .Select(p => p.KeyFaceId!.Value)
            .ToList();

        var thumbPaths = keyFaceIds.Count > 0
            ? await _ctx.Faces
                .Where(f => keyFaceIds.Contains(f.Id))
                .Select(f => new { f.Id, f.ThumbnailPath })
                .AsNoTracking()
                .ToDictionaryAsync(f => f.Id, f => f.ThumbnailPath)
            : new Dictionary<Guid, string?>();

        // Batch-fetch distinct photo counts per person in one GROUP BY query.
        var personIds = persons.Select(p => p.Id).ToList();
        var photoCounts = await _ctx.Faces
            .Where(f => f.PersonId.HasValue && personIds.Contains(f.PersonId!.Value))
            .GroupBy(f => f.PersonId!.Value)
            .Select(g => new { PersonId = g.Key, Count = g.Select(f => f.MediaFileId).Distinct().Count() })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.PersonId, x => x.Count);

        return persons
            .Select(p => new PersonSummary(
                p,
                p.KeyFaceId.HasValue && thumbPaths.TryGetValue(p.KeyFaceId.Value, out var tp) ? tp : null,
                photoCounts.GetValueOrDefault(p.Id, 0)))
            .OrderByDescending(s => s.Person.IsNamed)
            .ThenByDescending(s => s.PhotoCount)
            .ToList();
    }

    public async Task<int> GetPhotoCountAsync(Guid personId)
        => await _ctx.Faces
            .Where(f => f.PersonId == personId)
            .Select(f => f.MediaFileId)
            .Distinct()
            .CountAsync();

    public async Task MergeIntoAsync(Guid sourceId, Guid targetId)
    {
        // Re-link all faces from the source to the target.
        var faces = await _ctx.Faces.Where(f => f.PersonId == sourceId).ToListAsync();
        foreach (var f in faces)
            f.PersonId = targetId;

        // Recompute the target's FaceCount.
        var target = await _ctx.People.FirstOrDefaultAsync(p => p.Id == targetId);
        if (target != null)
            target.FaceCount = await _ctx.Faces.CountAsync(f => f.PersonId == targetId) + faces.Count;

        // Delete the source person.
        var source = await _ctx.People.FirstOrDefaultAsync(p => p.Id == sourceId);
        if (source != null)
            _ctx.People.Remove(source);

        await _ctx.SaveChangesAsync();
    }

    public async Task HideAsync(Guid personId)
    {
        var person = await _ctx.People.FirstOrDefaultAsync(p => p.Id == personId);
        if (person == null) return;
        person.IsVisible = false;
        await _ctx.SaveChangesAsync();
    }
}
