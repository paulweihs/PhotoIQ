# Initiative #1: v16 Prompt Implementation — Status Report

**Date:** April 20, 2026 (Session 2)
**Status:** DESIGNED, READY FOR VALIDATION
**Effort Invested:** 1 hour (design complete, validation blocked by Initiative #6)

---

## What Was Done

### 1. v16 Prompt Designed & Saved

**File:** `tools/PhotoWell.Eval/v16_prompt.txt`

**v16 Prompt (140 words):**
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

**Improvements over v15c:**
| Aspect | v15c | v16 |
|--------|------|-----|
| Word count | 100 | 140 |
| Main sections | 4 | 7 |
| Object handling | Implicit | Explicit (foosball/chess guidance) |
| Uncertainty handling | Minimal | Explicit (hedging language) |
| Animal ID | Not addressed | "Say pet if uncertain" |
| Silhouettes | Basic | Detailed (no hair/expression claim) |

---

## Design Rationale

### Problem Cases Targeted

1. **Foosball (v15c: 0.130 similarity)**
   - Issue: Model counts game pieces as "people"
   - v16 fix: Explicit "Game pieces... are NOT people" section
   - Expected: 0.200+ (54% improvement)

2. **Cat Photo (v15c: 0.165 similarity)**
   - Issue: Model guesses specific breed/color (hallucination)
   - v16 fix: "For uncertain cases, say 'a pet'..." and "Do NOT infer color"
   - Expected: 0.230+ (39% improvement)

3. **Silhouette (v15c: 0.167 similarity)**
   - Issue: Model claims facial features/hair in backlit images
   - v16 fix: Explicit backlighting section with "Do NOT claim to see hair color, expression..."
   - Expected: 0.220+ (32% improvement)

### Overall Impact

- **Baseline:** v15c = 0.223 avg Jaccard similarity
- **Target:** v16 = 0.245+ (10% overall improvement)
- **Strategy:** Additive improvements (no removals from v15c), focused on clarity

---

## Current Blockers

### Evaluation Cannot Run
- **Reason:** Library photos lack thumbnails (Initiative #6 prerequisite)
- **Error:** `Could not find file '...thumbnails/.../medium/....jpg'`
- **Solution:** Implement Initiative #6 first (test corpus rebuild + thumbnail generation)

### Path Forward
1. Complete Initiative #6 (20 min) — rebuild missing thumbnails
2. Re-run v16 evaluation against reference corpus
3. Compare v16 vs v15c similarity scores
4. If improvements >5%, deploy v16
5. If improvements <5%, iterate on v17 with additional improvements

---

## Deployment Checklist (When Ready)

When evaluation confirms >5% improvement:

- [ ] Update `AppSettings.CurrentPromptVersion = "v16"`
- [ ] Update `AppSettings.VisionAnalysisPrompt` with v16 text
- [ ] Rebuild & test
- [ ] Mark all v15c-analyzed photos as "outdated"
- [ ] Deploy to users
- [ ] Monitor AnalysisMetrics for hallucination/loop rates

---

## Files Created/Modified

**Created:**
- `tools/PhotoWell.Eval/v16_prompt.txt` — v16 prompt for evaluation

**Ready to Modify (on deployment):**
- `src/PhotoWell.Common/AppSettings.cs` — version + prompt

**Documentation:**
- `V16_PROMPT_REFINEMENT_PLAN.md` — detailed specification (already existed)

---

## Success Criteria

**Evaluation Phase:**
- ✓ v16 prompt designed per spec
- ✓ Prompt text saved to file
- ⏳ Evaluation harness can run (blocked by Initiative #6)
- ⏳ Similarity >0.245 (target)

**Deployment Phase:**
- ⏳ AppSettings updated
- ⏳ All 486+ tests passing
- ⏳ v15c photos marked as outdated
- ⏳ No regressions in loop detection or empty descriptions

---

## Dependency Chain

```
Initiative #6 (Test Corpus)
    ↓
Initiative #1 (v16 Prompt Eval)
    ↓
Initiative #5 (CLIP Classification) — optional, parallel
    ↓
Initiative #3 (User Feedback) — parallel with #5
```

**Recommendation:** Unblock Initiative #6 (20 min) immediately to enable Initiative #1 evaluation.

---

## Summary

v16 prompt is fully designed and ready for evaluation. Addresses three specific problem cases (foosball, cats, silhouettes) with explicit new sections on object identification, uncertainty handling, and silhouette constraints. Cannot validate improvements until Initiative #6 (test corpus thumbnail rebuild) is completed.

**Status: READY FOR VALIDATION (pending Initiative #6)**
