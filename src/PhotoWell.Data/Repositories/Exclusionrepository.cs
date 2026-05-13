
using Microsoft.EntityFrameworkCore;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Data;

namespace PhotoWell.Data.Repositories;

/// <summary>
/// Repository for ExclusionRule persistence and queries.
/// </summary>
/// <remarks>
/// ExclusionRules allow users to exclude specific folders or full paths from media scans.
/// Each rule is either:
/// - IsFullPath = false: folder name pattern (e.g., "node_modules", "temp") — matches any folder with that name
/// - IsFullPath = true: exact full path (e.g., "C:\Private") — matches only that specific path
///
/// Rules are combined with the built-in DefaultExclusions (Windows, AppData, .git, etc.) during imports.
/// Users can manage their exclusion rules through the ScanDrivesWindow UI.
/// </remarks>
public class ExclusionRepository : IExclusionRepository
{
    private readonly PhotoWellContext _context;

    /// <summary>Initializes a new instance of the ExclusionRepository.</summary>
    /// <param name="context">The PhotoWellContext for database access.</param>
    public ExclusionRepository(PhotoWellContext context)
    {
        _context = context;
    }

    /// <summary>Gets all exclusion rules, ordered by type (folder name, then full path).</summary>
    /// <returns>All ExclusionRule entities, sorted by IsFullPath, then by Value.</returns>
    public async Task<List<ExclusionRule>> GetAllAsync()
    {
        return await _context.ExclusionRules
            .OrderBy(e => e.IsFullPath)
            .ThenBy(e => e.Value)
            .ToListAsync();
    }

    /// <summary>Adds a new exclusion rule (folder name pattern or full path).</summary>
    /// <remarks>
    /// Failed writes detach the entity to keep the context usable. Errors are logged with full exception chain.
    /// </remarks>
    /// <param name="rule">The ExclusionRule to add (IsFullPath and Value must be set).</param>
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
            DbUpdateExceptionHandler.LogAndThrow(ex, "ExclusionRepository.AddAsync");
        }
    }

    /// <summary>Removes an exclusion rule by ID.</summary>
    /// <remarks>Silently succeeds even if the rule is not found (idempotent).</remarks>
    /// <param name="id">The ExclusionRule ID to remove.</param>
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
            DbUpdateExceptionHandler.LogAndThrow(ex, "ExclusionRepository.RemoveAsync");
        }
    }

    /// <summary>Removes an exclusion rule by its value and type (folder name or full path).</summary>
    /// <remarks>
    /// Used when the user removes a rule from the UI. Silently succeeds even if no matching rule is found.
    /// </remarks>
    /// <param name="value">The rule's value (folder name or full path).</param>
    /// <param name="isFullPath">Whether the rule is a full path (true) or folder name (false).</param>
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

    /// <summary>Replaces all exclusion rules with a new set.</summary>
    /// <remarks>
    /// Used when the user applies changes from the ScanDrivesWindow: deletes all current rules
    /// and inserts the new set atomically.
    /// </remarks>
    /// <param name="rules">The new set of ExclusionRules to store.</param>
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