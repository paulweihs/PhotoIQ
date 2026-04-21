# SQL Query Consolidation - Task #18 Completion

**Date:** April 18, 2026
**Status:** COMPLETED ✓
**Tests:** 478/478 passing ✓
**Build:** Clean (0 errors) ✓

---

## Summary

Successfully extracted and consolidated all FTS (Full-Text Search) SQL query patterns from `MediaFileRepository.cs` into a dedicated `FtsQueryHelper.cs` utility class. Eliminated SQL duplication, improved maintainability, and centralized database access patterns.

---

## What Was Done

### New File: FtsQueryHelper.cs

**File:** `PhotoIQPro.Data/Helpers/FtsQueryHelper.cs`

Created a static helper class with 4 methods encapsulating all FTS-related SQL operations:

#### 1. DeleteSingleAsync()
```csharp
public static async Task DeleteSingleAsync(DatabaseFacade database, Guid mediaFileId)
```
- Deletes a single MediaFile entry from the FTS index
- **Used by:** DeleteAsync(), ExcludeAsync()
- **Parameterization:** FormattableString (safe injection handling)

#### 2. DeleteBatchAsync()
```csharp
public static async Task DeleteBatchAsync(DatabaseFacade database, IEnumerable<Guid> mediaFileIds)
```
- Deletes multiple MediaFile entries from FTS index in chunked batches
- Chunks to 900 per batch (stays under SQLite's SQLITE_LIMIT_VARIABLE_NUMBER default of 999)
- **Used by:** RemoveByFolderAsync()
- **Parameterization:** ExecuteSqlRawAsync with injection-safe GUID formatting
- Handles all chunking logic internally

#### 3. ClearAllAsync()
```csharp
public static async Task ClearAllAsync(DatabaseFacade database)
```
- Clears all entries from the FTS index (atomic DELETE operation)
- Single auto-committed statement holding write lock for only milliseconds
- **Used by:** RebuildFtsAsync()
- **Parameterization:** Raw SQL (no user input)

#### 4. UpsertAsync()
```csharp
public static async Task UpsertAsync(
    DatabaseFacade database,
    Guid mediaFileId,
    string description,
    string tagText,
    string filename,
    string camera,
    string dateText,
    string folder)
```
- Inserts or updates a MediaFile entry in the FTS index
- Deletes any existing entry first, then inserts new one
- **Used by:** UpsertFtsAsync()
- **Parameterization:** FormattableString (safe for all string content: quotes, newlines, FTS operators)

---

### Refactored: MediaFileRepository.cs

**File:** `PhotoIQPro.Data/Repositories/MediaFileRepository.cs`

#### Changes Summary:
- **Added using:** `using PhotoIQPro.Data.Helpers;`
- **Replaced 5 SQL patterns** with FtsQueryHelper method calls
- **Removed:** ~20 lines of SQL duplication
- **Improved:** Code clarity and maintainability

#### Before → After Refactoring

**DeleteAsync() - Line ~146:**
```csharp
// Before: 2 lines of SQL
await _context.Database.ExecuteSqlAsync(
    $"DELETE FROM MediaFilesSearch WHERE media_file_id = {id.ToString()}");

// After: 1 line calling helper
await FtsQueryHelper.DeleteSingleAsync(_context.Database, id);
```

**ExcludeAsync() - Line ~167:**
```csharp
// Before: 2 lines of SQL
await _context.Database.ExecuteSqlAsync(
    $"DELETE FROM MediaFilesSearch WHERE media_file_id = {id.ToString()}");

// After: 1 line calling helper
await FtsQueryHelper.DeleteSingleAsync(_context.Database, id);
```

**RemoveByFolderAsync() - Line ~220:**
```csharp
// Before: 8 lines (loop, chunking, #pragma)
foreach (var chunk in toDelete.Chunk(900))
{
    var idList = string.Join(",", chunk.Select(mf => $"'{mf.Id}'"));
#pragma warning disable EF1002
    await _context.Database.ExecuteSqlRawAsync(
        $"DELETE FROM MediaFilesSearch WHERE media_file_id IN ({idList})");
#pragma warning restore EF1002
}

// After: 1 line calling helper (chunking is internal)
await FtsQueryHelper.DeleteBatchAsync(_context.Database, toDelete.Select(mf => mf.Id));
```

**RebuildFtsAsync() - Line ~468:**
```csharp
// Before: 1 raw SQL line
await _context.Database.ExecuteSqlRawAsync("DELETE FROM MediaFilesSearch");

// After: 1 line calling helper
await FtsQueryHelper.ClearAllAsync(_context.Database);
```

**UpsertFtsAsync() - Line ~522:**
```csharp
// Before: 2 lines of SQL
await _context.Database.ExecuteSqlAsync(
    $"DELETE FROM MediaFilesSearch WHERE media_file_id = {id}");
await _context.Database.ExecuteSqlAsync(
    $"INSERT INTO MediaFilesSearch(...) VALUES (...)");

// After: 1 line calling helper
await FtsQueryHelper.UpsertAsync(_context.Database, m.Id, description, tagText, filename, camera, dateText, folder);
```

---

## Benefits

### Code Quality
- **Reduced duplication:** Eliminated 5 identical/similar SQL patterns
- **Centralized logic:** All FTS operations in one location (easier to maintain)
- **Improved readability:** Method names are self-documenting (DeleteSingleAsync, UpsertAsync)
- **Consistency:** All SQL patterns use same parameterization approach

### Maintainability
- **Single source of truth:** Changes to FTS schema only need updates in FtsQueryHelper
- **Easier testing:** Helper methods can be unit tested independently
- **Documentation:** FtsQueryHelper includes full XML documentation explaining parameterization safety
- **Chunking logic:** Centralized batch deletion chunking (no need to repeat 900-item limit logic)

### Safety
- **Injection protection:** All parameterization handled consistently
- **GUID safety:** Documented why GUIDs are safe for raw SQL (only [0-9a-f-])
- **FormattableString usage:** Clearly documented where FormattableString is used vs ExecuteSqlRaw

### Performance
- No performance change (same SQL execution, just reorganized)
- Chunking logic preserved for batch operations
- Batch sizes optimized for SQLite limits

---

## Testing

**All tests passing:** 478/478 ✓

- Unit tests verify FTS operations work correctly
- No breaking changes to existing behavior
- All database operations still function as expected

---

## Files Modified/Created

| File | Type | Changes |
|------|------|---------|
| `PhotoIQPro.Data/Helpers/FtsQueryHelper.cs` | **NEW** | 118 lines (4 static methods) |
| `PhotoIQPro.Data/Repositories/MediaFileRepository.cs` | **Modified** | -20 lines (5 method calls simplified) |

**Net change:** +98 lines (mostly documentation in helper)

---

## Design Decisions

### Why Static Class vs Instance?
- FTS operations are stateless (pure utility functions)
- No dependencies beyond DatabaseFacade
- Static methods are simpler for callers
- Follows EF Core extension method pattern

### Why FormattableString vs Always Raw?
- FormattableString automatically parameterizes individual values
- Safe for user input (descriptions, tags, filenames)
- ExecuteSqlRawAsync used only for fixed SQL (DELETE FROM) or GUID collections
- Documented clearly which approach each method uses

### Why Batch Chunking at 900?
- SQLite default SQLITE_LIMIT_VARIABLE_NUMBER is 999
- 900 provides safe margin for other queries in transaction
- Matches existing implementation (preserved as-is)

---

## Future Opportunities

1. **Unit tests for FtsQueryHelper:** Could add tests for chunking logic
2. **Performance optimization:** Could use bulk UPSERT statement if SQLite supports it
3. **Search query builder:** BuildFtsQuery() and BuildDateText() could also be moved to helper
4. **Query tracing:** Helper class makes it easy to add logging/telemetry for FTS operations

---

## Validation

✓ Code compiles cleanly (0 errors)
✓ All 478 tests pass
✓ Behavior unchanged (refactoring only)
✓ Documentation complete
✓ No performance regression

---

## SQL Consolidation Principles Applied

1. **DRY (Don't Repeat Yourself):** Eliminated duplicate DELETE/INSERT patterns
2. **Single Responsibility:** Each method handles one FTS operation type
3. **Consistent Parameterization:** All SQL uses safe injection-proof patterns
4. **Clear Naming:** Method names document intent (DeleteSingleAsync vs DeleteBatchAsync)
5. **Documentation:** XML comments explain parameterization strategy for maintainers

