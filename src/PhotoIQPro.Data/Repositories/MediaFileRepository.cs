using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

public class MediaFileRepository : IMediaFileRepository
{
    private readonly PhotoIQContext _context;
    public MediaFileRepository(PhotoIQContext context) => _context = context;

    public async Task<MediaFile?> GetByIdAsync(Guid id) => await _context.MediaFiles.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == id);
    public async Task<IEnumerable<MediaFile>> GetAllAsync() => await _context.MediaFiles.OrderByDescending(m => m.DateTaken ?? m.DateImported).ToListAsync();
    public async Task<MediaFile> AddAsync(MediaFile entity) { await _context.MediaFiles.AddAsync(entity); await _context.SaveChangesAsync(); return entity; }
    public async Task UpdateAsync(MediaFile entity) { entity.DateModified = DateTime.UtcNow; _context.MediaFiles.Update(entity); await _context.SaveChangesAsync(); }
    public async Task DeleteAsync(Guid id) { var e = await _context.MediaFiles.FindAsync(id); if (e != null) { _context.MediaFiles.Remove(e); await _context.SaveChangesAsync(); } }
    public async Task<int> CountAsync() => await _context.MediaFiles.CountAsync();
    public async Task<MediaFile?> GetByPathAsync(string filePath) => await _context.MediaFiles.FirstOrDefaultAsync(m => m.FilePath == filePath);
    public async Task<MediaFile?> GetByHashAsync(string fileHash) => await _context.MediaFiles.FirstOrDefaultAsync(m => m.FileHash == fileHash);
    public async Task<IEnumerable<MediaFile>> GetFavoritesAsync() => await _context.MediaFiles.Where(m => m.IsFavorite).ToListAsync();
    public async Task<IEnumerable<MediaFile>> GetUnanalyzedAsync(int limit = 100) => await _context.MediaFiles.Where(m => !m.IsAnalyzed).Take(limit).ToListAsync();
    public async Task<IEnumerable<MediaFile>> SearchAsync(string query)
    {
        var keywords = query
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 3)
            .Distinct()
            .Take(10)
            .ToArray();

        if (keywords.Length == 0)
            return await GetAllAsync();

        // OR semantics: collect IDs that match any keyword in any field.
        var matchIds = new HashSet<Guid>();
        foreach (var kw in keywords)
        {
            var ids = await _context.MediaFiles
                .Where(m =>
                    EF.Functions.Like(m.FileName.ToLower(), $"%{kw}%") ||
                    (m.AiDescription != null && EF.Functions.Like(m.AiDescription.ToLower(), $"%{kw}%")) ||
                    (m.CameraModel != null && EF.Functions.Like(m.CameraModel.ToLower(), $"%{kw}%")) ||
                    (m.CameraMake != null && EF.Functions.Like(m.CameraMake.ToLower(), $"%{kw}%")) ||
                    m.Tags.Any(t => EF.Functions.Like(t.NormalizedName, $"%{kw}%")))
                .Select(m => m.Id)
                .ToListAsync();
            foreach (var id in ids) matchIds.Add(id);
        }

        if (matchIds.Count == 0)
            return [];

        // Load candidates with tags, then score and rank in memory.
        var candidates = await _context.MediaFiles
            .Include(m => m.Tags)
            .Where(m => matchIds.Contains(m.Id))
            .ToListAsync();

        return candidates
            .Select(m => (File: m, Score: Score(m, keywords)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.File.DateTaken ?? x.File.DateImported)
            .Select(x => x.File);
    }

    private static int Score(MediaFile m, string[] keywords)
    {
        int score = 0;
        var desc = m.AiDescription?.ToLowerInvariant() ?? "";
        var name = m.FileName.ToLowerInvariant();
        var camera = $"{m.CameraMake} {m.CameraModel}".ToLowerInvariant();
        var tagNames = m.Tags.Select(t => t.NormalizedName).ToList();

        foreach (var kw in keywords)
        {
            if (tagNames.Any(t => t.Contains(kw)))   score += 3;
            if (desc.Contains(kw))                    score += 2;
            if (name.Contains(kw))                    score += 1;
            if (camera.Contains(kw))                  score += 1;
        }
        return score;
    }
    public async Task<bool> ExistsAsync(string filePath) => await _context.MediaFiles.AnyAsync(m => m.FilePath == filePath);
}
