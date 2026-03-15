using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

public interface IExclusionRepository
{
    Task<List<ExclusionRule>> GetAllAsync();
    Task AddAsync(ExclusionRule rule);
    Task RemoveAsync(int id);
    Task ReplaceAllAsync(IEnumerable<ExclusionRule> rules);
}