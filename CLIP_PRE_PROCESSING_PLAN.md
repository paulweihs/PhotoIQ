# CLIP Pre-Processing for Vision Guidance — Plan

**Status:** PLANNING
**Objective:** Use CLIP object detection to classify images before vision inference, improving accuracy on problem cases

---

## Problem Statement

v15c evaluation revealed failure patterns that correlate with image content:

1. **Foosball/Game Tables** — Model sees game pieces, classifies as "people", creates confusion
2. **Animals** — Model misidentifies species (orange cat → small dog)
3. **Silhouettes** — Backlighting confuses model about subject details
4. **Objects** — Model hallucinates people in object-only photos

**Root cause:** Vision model (llama3.2-vision) doesn't have strong priors about image content type.

**Solution:** Use CLIP (already in system) to detect high-level image categories, then pass hints to vision prompt.

---

## Architecture

### Current Flow
```
Photo → Vision (llama3.2-vision) → Description
```

### Proposed Flow
```
Photo → CLIP Classification → Vision (with content hints) → Description
```

### CLIP Classification Output

Classify each photo into one or more categories:

```csharp
public class ImageClassification
{
    public bool ContainsPeople { get; set; }
    public bool ContainsAnimals { get; set; }
    public bool ContainsObjects { get; set; }
    public bool IsGameBoard { get; set; }
    public bool IsGamePieces { get; set; }
    public bool IsBacklit { get; set; }
    public bool IsIndoor { get; set; }
    public bool IsOutdoor { get; set; }
    public string? DominantObject { get; set; }  // "furniture", "toys", "food", etc.
}
```

**Implementation:** Use CLIP's existing TagVocabulary + custom probes

---

## Implementation Strategy

### Phase 1: Extend ClipTaggingService

Add image classification alongside existing tag generation:

```csharp
public class ClipTaggingService
{
    // Existing method
    public async Task<List<TagPrediction>> GenerateTagsAsync(...)

    // NEW: Image classification
    public async Task<ImageClassification> ClassifyImageAsync(
        PreparedImage image,
        CancellationToken ct = default)
    {
        var classifications = new ImageClassification();

        // Classify using CLIP embeddings + hardcoded thresholds
        var people_probe = await _textEngine.EncodeAsync("people, person, face, portrait", ct);
        var animals_probe = await _textEngine.EncodeAsync("cat, dog, bird, horse, animal, pet", ct);
        var game_probe = await _textEngine.EncodeAsync("game pieces, foosball, chess, checkers, board game", ct);
        var objects_probe = await _textEngine.EncodeAsync("furniture, toys, decorations, objects", ct);

        var imageEmbed = await _clipEngine.EmbedImageAsync(image, ct);

        classifications.ContainsPeople = CosineSimilarity(imageEmbed, people_probe) > 0.25;
        classifications.ContainsAnimals = CosineSimilarity(imageEmbed, animals_probe) > 0.30;
        classifications.IsGameBoard = CosineSimilarity(imageEmbed, game_probe) > 0.35;
        classifications.ContainsObjects = CosineSimilarity(imageEmbed, objects_probe) > 0.25;

        return classifications;
    }
}
```

### Phase 2: Integrate into Vision Pipeline

Modify ImportService.RunAnalysisAsync:

```csharp
private async Task<ImageUnderstanding> RunVisionInferenceAsync(...)
{
    // NEW: Get image classification
    var classification = await _tagging.ClassifyImageAsync(analysisPath, ct);

    // Build dynamic prompt hints based on classification
    var hints = BuildPromptHints(classification);

    // Append hints to base prompt
    var dynamicPrompt = AppSettings.VisionAnalysisPrompt + "\n" + hints;

    // Run vision with enhanced prompt
    var result = await _vision.AnalyzeImageAsync(visionPath, ct);

    return result;
}

private string BuildPromptHints(ImageClassification classification)
{
    var hints = new StringBuilder();

    if (classification.IsGameBoard)
        hints.AppendLine("This appears to be a game board or game pieces. Describe them as objects, not people.");

    if (!classification.ContainsPeople && !classification.ContainsAnimals)
        hints.AppendLine("This photo contains objects or environment only. Do NOT invent people or animals.");

    if (classification.IsBacklit)
        hints.AppendLine("The image has strong backlighting. Subjects may be silhouettes—describe only visible details.");

    return hints.ToString();
}
```

### Phase 3: Validation

Test on problem cases:
- Foosball: Should include "game board" hint
- Cat photo: Should classify as "animals", improve species identification
- Silhouette: Should classify as "backlit", adjust description expectations

---

## Technical Implementation Details

### CLIP-Based Classification

**Approach:** Encode probe texts, measure cosine similarity to image embedding

**Probes (text to encode):**
```csharp
static readonly Dictionary<string, float> ClassificationProbes = new()
{
    // People detection
    { "people, person, face, portrait, human", 0.25f },

    // Animals
    { "cat, cats, kitten, dog, dogs, puppy, bird, birds, horse, horses, animal, animals, pet, pets", 0.30f },

    // Game content
    { "foosball, game pieces, chess pieces, checkers, board game, game table, playing game", 0.35f },

    // Objects (when no people/animals)
    { "furniture, toys, objects, decorations, still life, arrangement", 0.25f },

    // Backlighting
    { "silhouette, backlit, backlighting, shadow, shadows, dark figure, bright background", 0.30f },

    // Location
    { "indoor, inside, interior, room, house, office, building", 0.28f },
    { "outdoor, outside, exterior, landscape, nature, sky, grass, trees, water", 0.28f }
};
```

**Encoding:** Use existing ClipTextEngine
```csharp
var embedding = await _clipEngine.EmbedTextAsync(probeText, ct);
```

**Similarity:** Cosine distance (already used in TagVocabulary)
```csharp
static float CosineSimilarity(float[] a, float[] b)
{
    var dotProduct = a.Zip(b, (x, y) => x * y).Sum();
    var normA = MathF.Sqrt(a.Sum(x => x * x));
    var normB = MathF.Sqrt(b.Sum(x => x * x));
    return dotProduct / (normA * normB);
}
```

---

## Enhanced Prompt Examples

### Example 1: Foosball (Game Board Detection)

**Base prompt:** (v15c prompt)

**Classification:** IsGameBoard = true

**Added hints:**
```
This appears to be a game board or game pieces. Describe them as objects, not people.
```

**Ollama input:** (v15c prompt) + hints
**Expected:** "Red and blue foosball figures arranged on rods" (not "people playing")

### Example 2: Cat Photo (Animal Detection)

**Classification:** ContainsAnimals = true, ContainsPeople = true

**Added hints:**
```
This photo contains animals. If you see a pet, describe its type (cat, dog, etc.) only if certain. If uncertain, say "a furry animal" or "a pet".
```

**Expected:** More accurate animal identification

### Example 3: Silhouette (Backlit Detection)

**Classification:** IsBacklit = true

**Added hints:**
```
The image has strong backlighting. Subjects may be silhouettes—describe only visible details. Do NOT claim to see hair color, expression, or details hidden in shadow.
```

**Expected:** "A silhouette of a person against bright foliage" (not specific details)

---

## Performance Impact

**Per-photo cost:**
- CLIP text encoding: ~10ms per probe × 6 probes = 60ms
- Image embedding: ~50ms (already done for tags)
- Cosine similarity: <1ms
- Prompt assembly: <1ms
- **Total new overhead: ~60ms per photo**

**Trade-off:** 60ms slower photo analysis, but fewer hallucinations (fewer re-analyses needed)

**Optimization:** Cache probe embeddings (compute once at startup)
```csharp
static readonly Dictionary<string, float[]> CachedProbes = new();

static void CacheProbes()
{
    foreach (var (probeText, _) in ClassificationProbes)
    {
        CachedProbes[probeText] = ClipTextEngine.Encode(probeText);  // Blocking, startup
    }
}
```

**With caching:** ~10ms total overhead (just similarity computations)

---

## Integration Points

### File: ClipTaggingService.cs
- Add `ClassifyImageAsync()` method
- Add `ImageClassification` class
- Add classification probes (static)

### File: ImportService.cs
- Add `BuildPromptHints()` method
- Modify `RunVisionInferenceAsync()` to get classification and append hints
- No database schema changes needed (hints are runtime only)

### File: AppSettings.cs
- NEW: Add `ClassificationProbes` static configuration
- No prompt change (hints are dynamic)

---

## Testing Strategy

### Unit Tests
```csharp
[Theory]
[InlineData("foosball_table.jpg", true, false)]  // Game board, no people
[InlineData("cat_photo.jpg", false, true)]       // Animal, no game board
[InlineData("silhouette_sunset.jpg", true, false)]  // Backlit
public async Task ClassifyImageAsync_ReturnsCorrectClassification(
    string photoPath, bool expectedGameBoard, bool expectedAnimals)
{
    var result = await _service.ClassifyImageAsync(photoPath);
    Assert.Equal(expectedGameBoard, result.IsGameBoard);
    Assert.Equal(expectedAnimals, result.ContainsAnimals);
}
```

### Integration Tests
```csharp
[Fact]
public async Task RunVisionInferenceAsync_WithGameBoardHint_DescribesPiecesAsObjects()
{
    var result = await _service.RunVisionInferenceAsync(foosballImagePath, ct);
    Assert.DoesNotContain("person", result.Description);
    Assert.Contains("game", result.Description.ToLower());
}
```

### Evaluation Tests
```bash
# Run v15c + CLIP classification on problem cases
dotnet run --project tools/PhotoIQPro.Eval -- \
  --optimize-prompt 50 \
  --prompt "$(cat tools/PhotoIQPro.Eval/v15_with_clip.txt)"

# Compare similarity scores for foosball, cats, silhouettes
```

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Hints confuse model (unhelpful/wrong) | MEDIUM | Start with conservative hints, test extensively |
| Performance degradation | LOW | Cache probe embeddings (10ms overhead only) |
| Probe threshold tuning (under-/over-trigger) | MEDIUM | Empirical tuning on evaluation set |
| New bugs in classification logic | LOW | Unit tests cover all classification paths |

---

## Implementation Timeline

### Phase 1: Probe Design & Testing (2 hours)
- Design CLIP probes for each category
- Unit test classification on 10-20 sample images
- Verify thresholds (sensitivity/specificity)

### Phase 2: Integration (1 hour)
- Add `ClassifyImageAsync()` to ClipTaggingService
- Add `BuildPromptHints()` to ImportService
- Integrate with RunVisionInferenceAsync

### Phase 3: Validation (1-2 hours)
- Run evaluation harness on v15c + hints
- Compare against v15c baseline
- A/B test on problem cases (foosball, cats, silhouettes)

**Total: 4-5 hours**

---

## Success Criteria

**v15c + CLIP classification is successful if:**
- ✓ Foosball photo similarity improves (0.130 → 0.200+)
- ✓ Cat photo similarity improves (0.165 → 0.230+)
- ✓ Silhouette photo similarity improves (0.167 → 0.220+)
- ✓ No regressions on good performers (>0.35 stays >0.30)
- ✓ Performance overhead <20ms per photo
- ✓ All 486+ tests still pass

---

## Future Enhancements

**v17+:**
1. Use CLIP object detection API (if available in Ollama/ONNX)
2. Multi-label classification (photo can be game board + indoor + people)
3. Confidence scores for hints (only add hints when confident)
4. Prompt branching (different base prompts for different content types)

---

## Summary

**CLIP pre-processing enables:**
- ✓ Content-aware vision prompts (game boards, animals, silhouettes)
- ✓ Reduced hallucinations on problem cases
- ✓ Targeted fixes without modifying base prompt
- ✓ Extensible framework for future improvements

**Implementation:** 4-5 hours, low risk, high potential impact (targeted at failure cases)

**Next:** Code this up after user feedback collection and test corpus expansion.
