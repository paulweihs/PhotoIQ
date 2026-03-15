
using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data.Repositories;

public class ExclusionRepository : IExclusionRepository
{
    private readonly PhotoIQContext _context;

    public ExclusionRepository(PhotoIQContext context)
    {
        _context = context;
    }

    public async Task<List<ExclusionRule>> GetAllAsync()
    {
        return await _context.ExclusionRules
            .OrderBy(e => e.IsFullPath)
            .ThenBy(e => e.Value)
            .ToListAsync();
    }

    public async Task AddAsync(ExclusionRule rule)
    {
        _context.ExclusionRules.Add(rule);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveAsync(int id)
    {
        var rule = await _context.ExclusionRules.FindAsync(id);
        if (rule is not null)
        {
            _context.ExclusionRules.Remove(rule);
            await _context.SaveChangesAsync();
        }
    }

    public async Task ReplaceAllAsync(IEnumerable<ExclusionRule> rules)
    {
        _context.ExclusionRules.RemoveRange(_context.ExclusionRules);
        _context.ExclusionRules.AddRange(rules);
        await _context.SaveChangesAsync();
    }
}