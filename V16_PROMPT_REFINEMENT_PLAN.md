# v16 Prompt Refinement Plan — April 20, 2026

**Status:** PLANNING
**Baseline:** v15c at 0.223 avg similarity
**Target:** v16 at 0.245+ avg similarity (10% improvement)

---

## v15c Performance Summary

### Evaluation Results (April 20, 2026)

**Dataset:** 10 available images from 35 ground truth corpus
**Average Jaccard Similarity:** 0.223
**Test Time:** ~1.3 minutes

**Best performers (>0.3 similarity):**
- 102d0f10c20d4d7fbbf5dcdaa8e5ff79.jpg (birthday cake): 0.402
- 9c4a169d415f43c39d1683124adddb07.jpg (child with bandage): 0.321
- 17c5fbd45a5e4c96b956a870e965981d.jpg (child with product box): 0.255

**Worst performers (<0.17 similarity):**
- 56ac040b5cfd4ba68d76c14d8189c911.jpg (foosball table): 0.130
- dd6140c22a75456ba3dff91a51256481.jpg (cat): 0.165
- 354ed0d2f4d54efc8d263e76dc1a2f0a.jpg (silhouette): 0.167

---

## Failure Analysis

### Pattern 1: Object Misidentification (Foosball)

**Ground truth:** "A foosball game with red and blue figures arranged on the rods."
**v15c output:** "A person playing a game of foosball... [repetition degeneration]"

**Root cause:** Model sees table + figures, classifies figures as people (person count was mandatory in v15, so model over-counts), then struggles when counting conflicts with description.

**v16 solution:** Add fallback for "no people" detection
```
If you see game pieces, toys, figurines, or objects arranged
on a table/board, describe them as objects first, then mention
any people present. Don't assume figures are people.
```

---

### Pattern 2: Animal Misidentification (Cat → Dog)

**Ground truth:** "An adult holding an orange cat while seated in an upholstered chair by a window with horizontal blinds."
**v15c output:** "One person sits in a chair with a small dog... The dog is a small, brown, and fluffy animal..."

**Root cause:** Color confusion (orange cat might look brown at thumbnail resolution), size inference wrong.

**v16 solution:** Add confidence guarding
```
When identifying animals, state the animal type only if certain.
If uncertain, say "a small furry animal" or "a pet" rather than
guessing species. Do not infer size from partial views.
```

---

### Pattern 3: Silhouette Handling (Still Imperfect)

**Ground truth:** "A child with bright backlighting from foliage creating a silhouette effect. Outdoor setting with trees."
**v15c output:** "A woman with long, wavy hair is standing in a bright, sunny environment. One person. She is facing the camera..."

**Root cause:** v15c silhouette rule is good but not emphasized enough. Gender enforcement converted "woman" → "person" but created inconsistent claim structure.

**v16 solution:** Expand silhouette section
```
SILHOUETTES & BACKLIGHTING: When subjects are backlit or very dark,
you may not see facial features clearly. In these cases:
- Describe as "a silhouette of a person" or "a shadowed figure"
- Do NOT claim to see hair color, expression, or facing direction
- Focus on what is visible: outline, general posture, setting
```

---

## v16 Candidate Improvements

### Tier 1: High Confidence (Expected 5-8% improvement)

**1. Object vs. Person Disambiguation**
```
Add ~30 words of guidance distinguishing people from:
- Game pieces/figurines (foosball, chess, action figures)
- Statues/sculptures
- Dolls/mannequins
- Silhouettes/shadows
```

**Implementation:** Add to main prompt, 1 new sentence in SILHOUETTES section
**Testing:** Re-run on foosball, statue photos
**Risk:** None (additive guidance)

**2. Confidence-Limited Claims**
```
When uncertain about details, use hedging language:
- "possibly" / "appears to be" instead of definite claims
- "a small furry animal" instead of "a dog"
- "obscured by angle/lighting" instead of speculation
```

**Implementation:** Add to "WHAT YOU SEE" section
**Testing:** Re-run on backlit, blurry, angled photos
**Risk:** Descriptions may be slightly less specific, but more accurate

**3. Color Uncertainty Reinforcement**
```
At thumbnail resolution, colors may appear different.
For animals: describe color if very obvious (white dog, black cat).
For clothing: omit specific colors unless dominant/obvious.
```

**Implementation:** Integrate into existing "WHAT YOU SEE" section
**Testing:** Re-run on animal and clothing photos
**Risk:** May reduce specificity of descriptions (acceptable for accuracy)

---

### Tier 2: Moderate Confidence (Expected 2-3% improvement)

**4. Multi-pass Person Counting (Optional)**
```
First pass: Count all people visible
Then: Describe the scene
Finally: Verify person count matches description

Allow model to self-correct before returning.
```

**Implementation:** More complex prompt structure, may confuse model
**Testing:** Needs careful calibration
**Risk:** Could increase loop detection if model gets stuck counting

**5. Image Type Hints (Optional)**
```
Detect high-level image type (portrait, landscape, group, object, animal)
and adjust prompt emphasis accordingly.

e.g., for "portrait": focus on visible person, omit environment
e.g., for "landscape": focus on setting, minimize person details
```

**Implementation:** Requires pre-processing step (costly)
**Testing:** Complex to validate
**Risk:** Over-fits to specific image types, fails on mixed scenes

---

### Tier 3: Low Confidence (Expected 1-2% improvement, high risk)

**6. Semantic Coherence Check**
```
Verify claimed objects/people appear in image description.
If photo described as "woman with dog", check that both appear
in noun phrases (not just mentioned once).
```

**Implementation:** Post-processing validation (CPU expensive)
**Testing:** May flag valid descriptions as false positives
**Risk:** High complexity, marginal benefit

---

## Recommended v16 Implementation Path

### Phase 1: Core Improvements (Low Risk, High Confidence)

**Changes to VisionAnalysisPrompt (add ~60 words):**

```
Describe this photo in 2-3 sentences for a searchable personal photo library.
Start with the main subject: "Two children playing", "A golden retriever", "Foosball figures on a table". Never start with "This" or "The image".

Use gender-neutral language: "person", "people", "child", "adult". Never use "man", "woman", "boy", "girl", or gendered pronouns.

DESCRIBE ONLY WHAT IS CLEARLY VISIBLE. Avoid guessing about details that are unclear, blurry, or too small to see. When uncertain, use hedging: "possibly", "appears to be", or "a small animal" instead of definite claims.

OBJECTS VS. PEOPLE: Game pieces, figurines, toys, and statues are NOT people. Describe them as objects. Do NOT count figures on game boards (foosball, chess) as people.

SILHOUETTES & BACKLIGHTING: When subjects are backlit or in shadow, describe what you see clearly—outline, posture, setting. Do NOT claim to see facial features, hair color, or expression in silhouettes.

ANIMALS: If uncertain about animal species, say "a pet" or "a small furry animal" rather than guessing. Omit specific colors unless obvious.

Write in prose (no lists), present tense.
```

**Word count:** ~140 words (up from 100 in v15c)
**Sections:** 7 (added 3, consolidated others)
**Risk level:** LOW (incremental improvements to existing sections)

### Phase 2: Evaluation

1. Update TestConfig.Prompt to v16 (if changing test harness)
2. Run 50-image evaluation on ground truth corpus
3. Compare worst matches vs v15c
4. Check for regressions (loop detection, empty descriptions)

### Phase 3: Deployment

If v16 shows >5% improvement:
1. Update AppSettings.CurrentPromptVersion = "v16"
2. Update VisionAnalysisPrompt
3. Rebuild and deploy
4. Mark photos analyzed with v15c as "outdated"

If v16 shows <5% improvement:
1. Keep v15c as baseline
2. Plan v17 with Tier 2 improvements (more aggressive)
3. Or defer further refinement until user feedback available

---

## Detailed v16 Prompt (Draft)

```
Describe this photo in 2-3 sentences for a searchable personal photo library.
Start with the main subject: "Two children playing", "A golden retriever", "Foosball figures on a table". Never start with "This" or "The image".

Use gender-neutral language: "person", "people", "child", "adult". Never use "man", "woman", "boy", "girl", or gendered pronouns.

WHAT YOU SEE: Describe only what is clearly visible. Avoid guessing about details that are unclear, blurry, or too small to see. When uncertain about identity, use hedging language: "possibly", "appears to be", or descriptive terms like "a small furry animal" or "a figure in dark clothing" rather than definite claims.

OBJECTS VS. PEOPLE: Game pieces, figurines, toys, board game pieces, statues, and dolls are NOT people. Describe them clearly as objects. Do not count figures on game boards (foosball, chess, checkers) as people playing.

SILHOUETTES & BACKLIGHTING: When subjects are backlit or in shadow, you cannot see facial features clearly. Describe the silhouette, outline, or posture visible. Do NOT claim to see hair color, expression, or details hidden in shadow.

ANIMALS: Identify common animals with confidence (dog, cat, bird, horse). For uncertain cases, say "a pet", "a small furry animal", or "an unidentified creature" rather than guessing. Do NOT infer color from poor lighting; omit color unless very obvious.

Write in prose (no lists), present tense.
```

---

## Testing Strategy

### Unit Tests (TextNormalizer)

Add tests to verify gender enforcement covers new terms:
```csharp
[Theory]
[InlineData("A woman with a dog", "A person with a dog")]
[InlineData("His cat is sleeping", "Their cat is sleeping")]
[InlineData("An actress on stage", "A performer on stage")]
public void EnforceGenderNeutrality_ReplacesTerms(string input, string expected)
    => Assert.Equal(expected, TextNormalizer.EnforceGenderNeutrality(input));
```

**Action:** Add 5-10 new test cases covering Tier 1 improvements

### Evaluation Harness

```bash
# Run v16 prompt against ground truth corpus
dotnet run --project tools/PhotoWell.Eval -- \
  --optimize-prompt 35 \
  --prompt "$(cat tools/PhotoWell.Eval/v16_prompt.txt)"
```

**Success criteria:**
- Avg similarity: >0.245 (10% improvement over v15c's 0.223)
- Worst matches: >0.150 (up from v15c's 0.130)
- Best matches: >0.350 (maintained)
- No new loop detections
- No increase in empty descriptions

### Spot Checks (Manual)

1. **Foosball photo:** Should describe game pieces as "red and blue figures" not "person"
2. **Cat photo:** Should say "pet" or "a furry animal" if color/species unclear
3. **Silhouette photo:** Should NOT claim to see hair color or facial expression
4. **Group photo:** Should count people accurately (no undercounting)

---

## Rollback Plan

If v16 underperforms:

```csharp
// AppSettings.cs
public const string CurrentPromptVersion = "v15c";
public const string VisionAnalysisPrompt = /* v15c text */;
```

Then rebuild and redeploy. Photos analyzed with v16 remain marked for historical tracking.

---

## Timeline

| Date | Event |
|------|-------|
| Apr 20 | v15c deployed, baseline established (0.223) |
| Apr 21-May 4 | v15c production monitoring (2 weeks) |
| May 5-10 | v16 prompt drafting & testing (1 week) |
| May 11-14 | Evaluation harness run on ground truth corpus |
| May 15 | Decision: deploy v16 or stay with v15c |
| May 16+ | If v16: deploy; if v15c: plan v17 |

---

## Success Metrics

**v16 is successful if:**
- ✓ Avg similarity improves to 0.245+ (10% over v15c)
- ✓ Foosball/cat/silhouette worst cases improve >0.05
- ✓ No regressions in best performers (still >0.35)
- ✓ Loop detection <1% (not worse)
- ✓ Empty descriptions <5% (not worse)
- ✓ All 486+ tests still pass

**v16 is unsuccessful if:**
- ✗ Avg similarity drops or stays <0.225
- ✗ Loop detection >2%
- ✗ New test failures
- ✗ Empty descriptions >10%

---

## Future Versions (v17+)

If v16 is successful, next improvements:
1. **v17:** Tier 2 improvements (multi-pass counting, image type hints)
2. **v18:** Semantic post-processing (object verification)
3. **v19+:** Model-specific optimizations (different prompts for llama vs moondream vs minicpm)

If v16 plateaus, consider:
1. **Backend:** Pre-processing with CLIP object detection before description
2. **Multi-modal:** Use both CLIP embeddings and Ollama vision for cross-validation
3. **A/B testing:** Compare different prompt approaches on user library

---

## Summary

**v16 Plan:**
- ✓ Based on v15c failure analysis (foosball, cats, silhouettes)
- ✓ Incremental improvements to existing sections (low risk)
- ✓ Estimated 10% improvement over v15c (0.223 → 0.245)
- ✓ Tier 1 confidence (object/person, confidence guards, color uncertainty)
- ✓ Testing ready (evaluation harness, spot checks, unit tests)
- ✓ Timeline: 2-3 weeks from now (May 5-15)

**Next action:** Deploy v15c to production, monitor for 2 weeks, then evaluate v16.
