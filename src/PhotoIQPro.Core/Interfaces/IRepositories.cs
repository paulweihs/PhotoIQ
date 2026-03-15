using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(Guid id);
    Task<int> CountAsync();
}

public interface IMediaFileRepository : IRepository<MediaFile>
{
    Task<MediaFile?> GetByPathAsync(string filePath);
    Task<MediaFile?> GetByHashAsync(string fileHash);
    Task<IEnumerable<MediaFile>> GetFavoritesAsync();
    Task<IEnumerable<MediaFile>> GetUnanalyzedAsync(int limit = 100);
    Task<IEnumerable<MediaFile>> SearchAsync(string query);
    Task<bool> ExistsAsync(string filePath);
}
