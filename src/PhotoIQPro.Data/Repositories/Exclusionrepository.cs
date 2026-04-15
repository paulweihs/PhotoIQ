
using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Common;
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
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // Detach the failed entity so this context instance remains usable.
            _context.Entry(rule).State = EntityState.Detached;
            var inner = ex.InnerException;
            var chain = ex.Message;
            while (inner != null) { chain += " -> " + inner.Message; inner = inner.InnerException; }
            AppLog.Error($"ExclusionRepository.AddAsync failed: {chain}");
            throw;
        }
    }

    public async Task RemoveAsync(int id)
    {
        var rule = await _context.ExclusionRules.FindAsync(id);
        if (rule is null) return;
        _context.ExclusionRules.Remove(rule);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _context.Entry(rule).State = EntityState.Unchanged;
            var inner = ex.InnerException;
            var chain = ex.Message;
            while (inner != null) { chain += " -> " + inner.Message; inner = inner.InnerException; }
            AppLog.Error($"ExclusionRepository.RemoveAsync failed: {chain}");
            throw;
        }
    }

    public async Task RemoveByValueAsync(string value, bool isFullPath)
    {
        var rule = await _context.ExclusionRules
            .FirstOrDefaultAsync(r => r.Value == value && r.IsFullPath == isFullPath);
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

    public async Task UpdateAsync(int id, string newValue, bool newIsFullPath)
    {
        var rule = await _context.ExclusionRules.FindAsync(id);
        if (rule is null) return;
        rule.Value      = newValue;
        rule.IsFullPath = newIsFullPath;
        await _context.SaveChangesAsync();
    }
}