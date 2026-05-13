using PhotoWell.Core.Models;

namespace PhotoWell.Core.Interfaces;

/// <summary>
/// Manages exclusion rules that prevent folders and files from appearing in media scans.
/// </summary>
/// <remarks>
/// Exclusion rules allow users to skip specific folders (by name pattern or full path) during imports.
/// Rules are evaluated during DriveService scans and combined with built-in defaults (Windows, AppData, .git, etc.).
/// Users manage rules through the ScanDrivesWindow UI.
/// </remarks>
public interface IExclusionRepository
{
    /// <summary>Gets all exclusion rules, sorted by type (folder name patterns first, then full paths).</summary>
    /// <returns>All ExclusionRule entities in the database.</returns>
    Task<List<ExclusionRule>> GetAllAsync();

    /// <summary>Adds a new exclusion rule (folder name pattern or exact full path).</summary>
    /// <param name="rule">The ExclusionRule to add (IsFullPath and Value must be set).</param>
    Task AddAsync(ExclusionRule rule);

    /// <summary>Removes an exclusion rule by ID.</summary>
    /// <remarks>Silently succeeds even if the rule does not exist (idempotent).</remarks>
    /// <param name="id">The ExclusionRule ID to remove.</param>
    Task RemoveAsync(int id);

    /// <summary>Removes an exclusion rule by its value and type.</summary>
    /// <remarks>Used by the ScanDrivesWindow UI to remove rules without needing their database ID.</remarks>
    /// <param name="value">The rule's value (folder name pattern or full path).</param>
    /// <param name="isFullPath">Whether the rule is a full path (true) or folder name pattern (false).</param>
    Task RemoveByValueAsync(string value, bool isFullPath);

    /// <summary>Replaces all exclusion rules with a new set atomically.</summary>
    /// <remarks>
    /// Used when the user clicks "Apply" in ScanDrivesWindow: deletes all existing rules and inserts
    /// the new set in a single operation to ensure consistency.
    /// </remarks>
    /// <param name="rules">The new ExclusionRule collection to persist.</param>
    Task ReplaceAllAsync(IEnumerable<ExclusionRule> rules);

    /// <summary>Updates an exclusion rule's value and type.</summary>
    /// <param name="id">The ExclusionRule ID to update.</param>
    /// <param name="newValue">The new value (folder name or full path).</param>
    /// <param name="newIsFullPath">Whether the updated rule is a full path (true) or folder name (false).</param>
    Task UpdateAsync(int id, string newValue, bool newIsFullPath);
}