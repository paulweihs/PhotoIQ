using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Matches face embeddings against known persons using cosine similarity.
///
/// Thresholds (ArcFace / MobileFaceNet cosine similarity, higher = more similar):
///   HIGH ≥ 0.65 — auto-link; person confirmed without user input.
///   LOW  ≥ 0.40 — soft match; linked but Phase 3 will ask the user to confirm.
///   below LOW   — new unnamed Person cluster created.
///
/// Registered as Singleton so the embedding cache survives across import sessions.
/// Uses IServiceScopeFactory for all DB access to avoid a scoped-from-singleton problem.
/// </summary>
public sealed class FaceRecognitionService : IFaceRecognitionService
{
    private const float HighThreshold = 0.65f;
    private const float LowThreshold  = 0.40f;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    // In-memory cache: (PersonId, normalised embedding float[])
    // Null = not yet loaded; invalidated when persons change.
    private List<(Guid PersonId, float[] Embedding)>? _cache;

    public FaceRecognitionService(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    public void InvalidateCache()
    {
        _cacheLock.Wait();
        try { _cache = null; }
        finally { _cacheLock.Release(); }
    }

    public async Task IdentifyFaceAsync(Face face, CancellationToken ct = default)
    {
        if (face.Embedding == null || face.Embedding.Length == 0) return;

        try
        {
            var faceEmb = BytesToFloats(face.Embedding);
            var embeddings = await GetCachedEmbeddingsAsync(ct);

            float bestSim = 0;
            Guid? bestId  = null;

            foreach (var (personId, emb) in embeddings)
            {
                float sim = CosineSimilarity(faceEmb, emb);
                if (sim > bestSim) { bestSim = sim; bestId = personId; }
            }

            using var scope = _scopeFactory.CreateScope();
            var personRepo = scope.ServiceProvider.GetRequiredService<IPersonRepository>();

            if (bestSim >= HighThreshold && bestId.HasValue)
            {
                await personRepo.LinkFaceToPersonAsync(face.Id, bestId.Value, bestSim);
                await UpdateAverageEmbeddingAsync(personRepo, bestId.Value, faceEmb);
            }
            else if (bestSim >= LowThreshold && bestId.HasValue)
            {
                // Soft match — link but mark with lower confidence for Phase 3 confirmation
                await personRepo.LinkFaceToPersonAsync(face.Id, bestId.Value, bestSim);
                await UpdateAverageEmbeddingAsync(personRepo, bestId.Value, faceEmb);
            }
            else
            {
                // No match — create a new unnamed cluster
                var newPerson = new Person
                {
                    KeyFaceId       = face.Id,
                    AverageEmbedding = FloatsToBytes(faceEmb),
                    FaceCount       = 1,
                };
                await personRepo.CreateAsync(newPerson);
                await personRepo.LinkFaceToPersonAsync(face.Id, newPerson.Id, 1.0);
                InvalidateCache(); // new person means cache must refresh
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Recognition failure is non-fatal — face stays unassigned.
            AppLog.Error($"FaceRecognitionService.IdentifyFaceAsync failed for face {face.Id}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<(Guid, float[])>> GetCachedEmbeddingsAsync(CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache != null) return _cache;

            using var scope = _scopeFactory.CreateScope();
            var personRepo = scope.ServiceProvider.GetRequiredService<IPersonRepository>();
            var rows = await personRepo.GetAllEmbeddingsAsync();

            _cache = rows.Select(r => (r.PersonId, BytesToFloats(r.Embedding))).ToList();
            return _cache;
        }
        finally { _cacheLock.Release(); }
    }

    // ── Average embedding ────────────────────────────────────────────────────

    private async Task UpdateAverageEmbeddingAsync(IPersonRepository repo, Guid personId, float[] newEmb)
    {
        var person = await repo.GetByIdAsync(personId);
        if (person == null) return;

        if (person.AverageEmbedding == null || person.FaceCount == 0)
        {
            person.AverageEmbedding = FloatsToBytes(newEmb);
            person.FaceCount        = 1;
        }
        else
        {
            var avg = BytesToFloats(person.AverageEmbedding);
            int n   = person.FaceCount;
            // Incremental mean: avg_new[i] = avg[i] + (newEmb[i] - avg[i]) / (n + 1)
            for (int i = 0; i < avg.Length && i < newEmb.Length; i++)
                avg[i] += (newEmb[i] - avg[i]) / (n + 1);
            Normalize(avg);
            person.AverageEmbedding = FloatsToBytes(avg);
            person.FaceCount        = n + 1;
        }

        await repo.UpdateAsync(person);

        // Update cached entry
        await _cacheLock.WaitAsync();
        try
        {
            if (_cache != null)
            {
                var updatedEmb = BytesToFloats(person.AverageEmbedding);
                var idx = _cache.FindIndex(e => e.PersonId == personId);
                if (idx >= 0) _cache[idx] = (personId, updatedEmb);
                else          _cache.Add((personId, updatedEmb));
            }
        }
        finally { _cacheLock.Release(); }
    }

    // ── Math helpers ──────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0;
        for (int i = 0; i < len; i++) dot += a[i] * b[i];
        // Embeddings are already unit-normalised by FaceDetectionEngine
        return dot;
    }

    private static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
