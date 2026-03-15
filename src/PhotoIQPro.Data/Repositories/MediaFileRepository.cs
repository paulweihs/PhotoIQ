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
    public async Task<IEnumerable<MediaFile>> SearchAsync(string query) => await _context.MediaFiles.Include(m => m.Tags).Where(m => m.FileName.ToLower().Contains(query.ToLower()) || m.Tags.Any(t => t.NormalizedName.Contains(query.ToLower()))).ToListAsync();
    public async Task<bool> ExistsAsync(string filePath) => await _context.MediaFiles.AnyAsync(m => m.FilePath == filePath);
}
