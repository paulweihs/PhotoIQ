using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

public class PersonRepository : IPersonRepository
{
    private readonly PhotoIQContext _ctx;
    public PersonRepository(PhotoIQContext ctx) => _ctx = ctx;

    public async Task<Person> CreateAsync(Person person)
    {
        _ctx.People.Add(person);
        await _ctx.SaveChangesAsync();
        return person;
    }

    public async Task UpdateAsync(Person person)
    {
        person.DateModified = DateTime.UtcNow;
        _ctx.People.Update(person);
        await _ctx.SaveChangesAsync();
    }

    public async Task<Person?> GetByIdAsync(Guid id)
        => await _ctx.People.FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IReadOnlyList<Person>> GetAllAsync()
        => await _ctx.People.Where(p => p.IsVisible).OrderBy(p => p.Name).ToListAsync();

    public async Task<IReadOnlyList<Person>> GetAllNamedAsync()
        => await _ctx.People.Where(p => p.IsNamed && p.IsVisible).OrderBy(p => p.Name).ToListAsync();

    public async Task<IReadOnlyList<(Guid PersonId, byte[] Embedding)>> GetAllEmbeddingsAsync()
    {
        var rows = await _ctx.People
            .Where(p => p.AverageEmbedding != null)
            .Select(p => new { p.Id, p.AverageEmbedding })
            .AsNoTracking()
            .ToListAsync();
        return rows.Select(r => (r.Id, r.AverageEmbedding!)).ToList();
    }

    public async Task LinkFaceToPersonAsync(Guid faceId, Guid personId, double confidence)
    {
        var face = await _ctx.Faces.FirstOrDefaultAsync(f => f.Id == faceId);
        if (face == null) return;
        face.PersonId                  = personId;
        face.IdentificationConfidence  = confidence;
        await _ctx.SaveChangesAsync();
    }

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
