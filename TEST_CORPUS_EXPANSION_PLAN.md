# Test Corpus Expansion — Implementation Plan

**Status:** PLANNING
**Objective:** Expand evaluation ground truth corpus from 10 to 50+ images for more reliable prompt validation

---

## Current State

**Ground truth corpus:** 10 images with thumbnail files
- Located in: `C:\Users\retli\AppData\Local\PhotoIQPro\thumbnails\{prefix}\medium\{filename}.jpg`
- Database: evaluations.db `reference_corpus` table
- Descriptions: Claude-generated (high quality baseline)

**Issue:** 25 additional images ingested but missing thumbnail files
- Files reference non-existent paths
- Ollama evaluation skips these (file not found errors)
- Can't run full 50-image evaluation

---

## Root Cause Analysis

### How Thumbnails Are Created

**PhotoIQ app generates thumbnails during import:**

```csharp
// ThumbnailService.GenerateAsync()
// Creates 3 sizes:
// - 150px: Small preview (not used for vision)
// - 400px: Medium (used in gallery details)
// - 800px: Large (used for vision analysis)
```

**Location:** `{AppDataPath}/thumbnails/{prefix}/medium/{filename}.jpg`

### Why 25 Images Don't Have Thumbnails

1. Added directly to evaluation database from JSON files
2. Didn't go through PhotoIQ import pipeline (which generates thumbnails)
3. Referenced non-existent files in filesystem

---

## Solution Approaches

### Approach A: Generate Thumbnails from Source Files (Recommended)

**Workflow:**

1. Find original photo files in library
2. Re-generate thumbnails using ThumbnailService
3. Update ground truth database with correct paths

**Implementation:**

```csharp
// In PromptEval project
public class ThumbnailGenerator
{
    private readonly ThumbnailService _thumbnailService;

    public async Task EnsureThumbailsExistAsync(
        List<string> mediaFileIds,
        CancellationToken ct = default)
    {
        var mediaFileRepo = GetRepository();

        foreach (var id in mediaFileIds)
        {
            var mf = await mediaFileRepo.GetByIdAsync(Guid.Parse(id));
            if (mf == null) continue;

            // Check if thumbnail exists
            if (!File.Exists(mf.ThumbnailLarge))
            {
                // Re-generate
                await _thumbnailService.GenerateAsync(mf, ct);
            }
        }
    }
}
```

**Pros:**
- Uses existing ThumbnailService (tested code)
- Generates proper thumbnail hierarchy
- Metadata preserved (dates, orientation)

**Cons:**
- Requires source files to be accessible
- Time-consuming (one per photo)
- Depends on ThumbnailService availability

### Approach B: Copy Existing Thumbnails (Fast)

**Workflow:**

1. For each missing ground truth image
2. Copy an existing thumbnail to the target path
3. Use generic placeholder (doesn't matter for eval)

**Implementation:**

```bash
# For each missing image, copy existing thumbnail
cp /path/to/existing/thumb.jpg \
   /path/to/missing/thumbnail/medium/{missing_filename}.jpg
```

**Pros:**
- Very fast (just file copies)
- No dependencies
- Good enough for evaluation (image content doesn't matter, just needs to exist)

**Cons:**
- Evaluation uses wrong images (evaluation becomes invalid)
- Won't actually test vision prompt on the intended content

### Approach C: Create Synthetic Thumbnails (Workaround)

**Workflow:**

1. Create empty placeholder images
2. Place at correct paths
3. Marks in code that these are synthetic

**Pros:**
- Fastest (empty files)
- No dependencies

**Cons:**
- Vision inference will fail (empty image)
- Useless for evaluation

---

## Recommended Implementation: Approach A + B Hybrid

### Phase 1: Inventory (10 min)

Identify which 25 images have source files still accessible:

```sql
SELECT gf.file_name, mf.file_path
FROM reference_corpus gf
LEFT JOIN media_files mf ON mf.file_name = gf.file_name
WHERE gf.id > 10  -- The 25 we added
```

**Expected:**
- ~15 images: Found in media_files table (source file path known)
- ~10 images: Not in media_files (orphaned, source path unknown)

### Phase 2: Regenerate Thumbnails (30 min)

For the 15 found images:

```csharp
// For each image in reference_corpus that's also in media_files
var missingThumbResults = (
    from gf in groundTruthCorpus
    join mf in mediaFiles on gf.FileName equals mf.FileName
    where !File.Exists(mf.ThumbnailLarge)
    select mf
).ToList();

foreach (var mf in missingThumbResults)
{
    await _thumbnailService.GenerateAsync(mf, cancellationToken);
    Console.WriteLine($"Generated thumbnail for {mf.FileName}");
}
```

**Time:** ~30 seconds per image = 8 minutes for 15 images

### Phase 3: Use Public Test Photos (20 min)

For the 10 orphaned images (no source file):

Option 1: Remove from corpus
```sql
DELETE FROM reference_corpus
WHERE file_name IN (orphaned_list)
```

Option 2: Find replacement images with good descriptions
- Use 10 random photos from PhotoIQ library that have good descriptions
- Generate Claude descriptions for them (10 × 30sec = 5 min)
- Add to ground truth corpus

---

## Detailed Implementation Plan

### Step 1: Query to Find Orphaned Images

```sql
-- Find ground truth images with no corresponding media file
SELECT gf.file_name, gf.description
FROM reference_corpus gf
WHERE NOT EXISTS (
    SELECT 1 FROM media_files mf
    WHERE mf.file_name = gf.file_name
)
ORDER BY gf.file_name
```

### Step 2: Regenerate Thumbnails for Found Images

**File:** `tools/PhotoIQPro.Eval/ThumbnailGeneratorTool.cs` (new)

```csharp
using PhotoIQPro.Services.Thumbnails;
using PhotoIQPro.Data;

public class ThumbnailGeneratorTool
{
    public static async Task Main(string[] args)
    {
        var dbPath = args.Length > 0 ? args[0] :
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoIQPro", "photoiq.db");

        var options = new DbContextOptionsBuilder<PhotoIQContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Build();

        using var context = new PhotoIQContext(options);
        var mediaRepo = new MediaFileRepository(context);
        var thumbService = new ThumbnailService();

        var missingCount = 0;
        var generatedCount = 0;

        var allPhotos = await mediaRepo.GetAllAsync();

        foreach (var mf in allPhotos)
        {
            if (!File.Exists(mf.ThumbnailLarge))
            {
                missingCount++;
                try
                {
                    // Regenerate from source
                    if (File.Exists(mf.FilePath))
                    {
                        await thumbService.GenerateAsync(mf, CancellationToken.None);
                        generatedCount++;
                        Console.WriteLine($"✓ {mf.FileName}");
                    }
                    else
                    {
                        Console.WriteLine($"✗ {mf.FileName} — source not found");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ {mf.FileName} — {ex.Message}");
                }
            }
        }

        Console.WriteLine($"\nSummary: {missingCount} missing, {generatedCount} generated");
    }
}
```

**Run:**
```bash
dotnet run --project tools/PhotoIQPro.Eval -- regenerate-thumbnails
```

### Step 3: Update Orphaned Entries (or Delete)

For images with no source file, either:

**Option A: Delete** (cleaner, recommended)
```sql
DELETE FROM reference_corpus
WHERE file_name NOT IN (
    SELECT file_name FROM media_files
)
```

**Option B: Replace** with alternative images
- Sample 10 random photos from library
- Generate Claude descriptions (using Claude API or copy from existing)
- Insert into reference_corpus with new image paths

---

## Testing the Expansion

### Validation Query

```sql
-- Verify all ground truth images have thumbnails
SELECT gf.file_name, gf.image_used
FROM reference_corpus gf
WHERE NOT EXISTS (
    SELECT 1 FROM sqlite_master
    WHERE type = 'file'
    AND name = gf.image_used
)
```

Expected result: Empty (all files exist)

### Run Evaluation

```bash
# With expanded corpus (now 35 or more images)
cd /c/Dev/PhotoIQPro
PROMPT=$(cat tools/PhotoIQPro.Eval/v15c_prompt.txt)
dotnet run --project tools/PhotoIQPro.Eval -- \
  --optimize-prompt 50 --prompt "$PROMPT"
```

Expected:
- All 35+ ground truth images processed
- No "file not found" errors
- Similarity scores computed for all
- Better statistical confidence (large N)

---

## Alternative: Use Only Existing 10 Images

**If rebuilding thumbnails is problematic:**

Accept that evaluation corpus is small (10 images) and:
1. **Acknowledge limitation** in documentation
2. **Plan quarterly expansion** (collect new ground truth each quarter)
3. **Use current 10 for baseline comparisons** (v15c vs v16 trends visible even with small N)
4. **Focus on spot-checks** rather than statistical analysis

**Timeline:** 0 minutes (no work needed)
**Trade-off:** Less confident evaluation metrics, but still usable for iteration

---

## Timeline

### Option 1: Rebuild Thumbnails
- Inventory: 5 min
- Regenerate: 10 min
- Validate: 5 min
- **Total: 20 minutes**
- **Result: 35+ image corpus, high confidence evaluation**

### Option 2: Delete Orphans
- Query: 2 min
- Delete: 1 min
- Validate: 2 min
- **Total: 5 minutes**
- **Result: 10 image corpus (current state), low-medium confidence**

### Option 3: Accept Current State
- **Total: 0 minutes**
- **Result: Work with 10 images, document limitation**

---

## Recommendation

**Best path forward: Option 1 (rebuild thumbnails)**

Rationale:
- Small time investment (20 min)
- Unlocks larger evaluation corpus
- Enables confident v15c vs v16 comparison
- Re-usable for future quarterly expansions

**Fallback: Option 2 (delete orphans) if rebuild fails**

---

## Success Criteria

**Expanded corpus is successful if:**
- ✓ All reference_corpus images have corresponding thumbnail files
- ✓ Evaluation harness processes all images without file-not-found errors
- ✓ 35+ images available for similarity comparison
- ✓ v15c vs v16 comparison has statistical confidence

---

## Future: Quarterly Expansion

**Process for adding new ground truth each quarter:**

1. **Sample 20 new photos** from library (stratified by date)
2. **Generate Claude descriptions** for each (20 × $0.01 = $0.20 cost)
3. **Add to reference_corpus** with correct thumbnail paths
4. **Update evaluation baselines** with larger corpus

**Timeline:** 1 hour per quarter

---

## Summary

**Test corpus expansion:**
- Current: 10 images (✓ have thumbnails)
- Target: 35+ images (15 found, 10 orphaned)
- Time to fix: 20 minutes
- Impact: Confident evaluation of prompt versions

**Next: Implement Step 1 (inventory) to determine exact path forward.**
