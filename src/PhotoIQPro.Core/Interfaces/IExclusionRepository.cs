using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IExclusionRepository
{
    Task<List<ExclusionRule>> GetAllAsync();
    Task AddAsync(ExclusionRule rule);
    Task RemoveAsync(int id);
    Task RemoveByValueAsync(string value, bool isFullPath);
    Task ReplaceAllAsync(IEnumerable<ExclusionRule> rules);
    Task UpdateAsync(int id, string newValue, bool newIsFullPath);
}