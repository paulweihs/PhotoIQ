# Performance Optimization Plan

Generated: April 18, 2026 | Analysis Complete | 11 Bottlenecks Identified

## Quick Summary

**Estimated Cumulative Improvement:** 30-50% latency reduction on large libraries (10K+ photos), 60-70% memory reduction during batch operations.

**11 bottlenecks identified** across:
- Database query efficiency (N+1 patterns, missing indexes)
- Batch operation latency (redundant reloads, sync operations)
- Memory management (full library loads, temporary allocations)
- Async/await patterns (blocking waits, polling without backoff)
- Caching strategies (missing caches for read-heavy operations)
- UI performance (missing virtualization, synchronous thumbnail loading)

---

## HIGH-IMPACT OPTIMIZATIONS (Phase 1 - Start Here)

### 1. Fix Tag Photo Counts N+1 Query
**File:** `PhotoIQPro.Data/Repositories/MediaFileRepository.cs` (GetTagPhotoCountsAsync)
**Impact:** -98% queries on tag browser load (500+ queries → 1)
**Effort:** 1-2 hours
**Complexity:** Moderate (convert to raw SQL or GROUP BY aggregation)
**Expected Gain:** Tag popup load time <10ms (vs. 100-200ms currently)

### 2. Add Missing Tag Include to Vision Pending Query
**File:** `PhotoIQPro.Data/Repositories/MediaFileRepository.cs` (GetVisionPendingAsync)
**Impact:** -95% queries on vision re-analysis (1K queries → 1)
**Effort:** 30 minutes
**Complexity:** Easy (one-line .Include)
**Expected Gain:** 1000-file re-analysis -50% faster

### 3. Implement VirtualizingStackPanel in Photo Gallery
**File:** `PhotoIQPro.Desktop/Views/MainWindow.xaml`
**Impact:** -95% memory on large galleries, +200% scroll FPS
**Effort:** 1-2 hours
**Complexity:** Moderate
**Expected Gain:** Scroll from <10 FPS to 60 FPS, memory -500 MB on 10K gallery

### 4. Fix Blocking Wait in Face Cache Invalidation
**File:** `PhotoIQPro.Services/Vision/FaceRecognitionService.cs` (InvalidateCache)
**Impact:** +100% UI responsiveness during face detection
**Effort:** 30 minutes
**Complexity:** Easy (replace .Wait() with async or volatile flag)
**Expected Gain:** No more UI freezes during face detection

---

## DETAILED BOTTLENECK LIST

See [PERFORMANCE_BOTTLENECKS.md](./docs/PERFORMANCE_BOTTLENECKS.md) for full analysis with:
- Root cause analysis for each bottleneck
- Current code patterns
- Recommended fixes with code examples
- Expected latency/memory improvements
- Implementation complexity ratings

**Bottlenecks Covered:**
1. Tag photo counts N+1 pattern
2. Vision pending missing include
3. Redundant reload after tag strip (2 places)
4. Synchronous thumbnail generation in import loop
5. Full library load into memory
6. Face embedding conversion on every access
7. Untracked temp file accumulation
8. Blocking wait in cache invalidation
9. Polling without exponential backoff
10. Metrics recording fire-and-forget risk
11. Multiple caching opportunities

---

## MEASUREMENT & VALIDATION

**Before starting optimizations, establish baseline metrics:**

```csharp
// Measure database queries
int queryCount = 0;
using (var listener = new DiagnosticListener("Microsoft.EntityFrameworkCore.Database.Command"))
{
    listener.Subscribe(new QueryCountingObserver(x => queryCount++));
    // ... run operation
}
Debug.WriteLine($"Query count: {queryCount}");

// Measure memory
long memBefore = GC.GetTotalMemory(true);
// ... operation
long memAfter = GC.GetTotalMemory(false);
Debug.WriteLine($"Memory used: {(memAfter - memBefore) / 1024 / 1024} MB");

// Measure UI responsiveness (FPS)
var stopwatch = Stopwatch.StartNew();
// ... render 1000 items
stopwatch.Stop();
int fps = (int)(1000 / (stopwatch.ElapsedMilliseconds / 1000.0));
Debug.WriteLine($"FPS: {fps}");
```

**Key metrics to track:**
- Query count per operation (target: 1 query per logical operation)
- Peak memory during batch operations (target: <100 MB for 100K photos)
- Gallery scroll FPS (target: 60 FPS, >50 FPS acceptable)
- UI thread responsiveness (target: <16ms per frame)
- Tag popup latency (target: <50ms)

---

## IMPLEMENTATION SCHEDULE

### Week 1: Database & UI Performance
- Monday: Implement tag count aggregation fix + missing includes (4 hours)
- Tuesday: Add virtualization to gallery (3 hours)
- Wednesday: Fix blocking waits in cache (2 hours)
- Thursday-Friday: Testing & validation (8 hours)

### Week 2: Batch Operations & Memory
- Monday: Implement chunked library loading (4 hours)
- Tuesday-Wednesday: Refactor tag strip redundant reloads (4 hours)
- Thursday: Add embedding disk cache (3 hours)
- Friday: Integration testing (4 hours)

### Week 3: Polish & Optional
- Synchronous thumbnail generation refactoring (optional, hard)
- Caching improvements (tag list, exclusion rules)
- Performance monitoring hooks
- Documentation

---

## SUCCESS CRITERIA

✓ All 11 bottlenecks analyzed and documented
✓ Phase 1 (4 high-impact) optimizations implemented and tested
✓ 30%+ latency improvement on 10K+ photo operations
✓ 60%+ memory reduction on batch operations
✓ Gallery scroll FPS ≥50 with virtualization
✓ Zero UI freezes during face detection
✓ Performance metrics documented with before/after numbers

---

## NOTES

- All optimizations are **backward-compatible** — no API changes
- Prioritize **database query reduction** first (highest impact, easiest wins)
- **Virtualization** is critical for >5K photo galleries (do early)
- **Chunked loading** required for >50K photo libraries
- Avoid premature optimization of low-impact areas (e.g., path string formatting)

