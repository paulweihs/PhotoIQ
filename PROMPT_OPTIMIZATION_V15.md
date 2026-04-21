# Vision Prompt Optimization: v14 → v15

**Date:** April 19, 2026
**Status:** COMPLETE ✓
**Tests:** 486/486 passing
**Build:** Clean (0 errors, 1 warning)

---

## Overview

Implemented all 5 recommended prompt fixes based on the evaluation of 100 test photos. Upgrade from Prompt v14 to v15 strengthens guidance on:

1. **Person counting** — now mandatory and emphasizes "don't omit anyone"
2. **Hallucination guardrails** — explicit ban on speculating about unclear details
3. **Blur/angle handling** — guidance for non-frontal and blurry images
4. **Repetition prevention** — explicit instruction to stop if repeating
5. **Gender neutrality** — reinforced existing guardrail

---

## Prompt v15 Changes

### Before (v14 - 95 words)
```
Describe this photo in 2-3 sentences for a searchable personal photo library.
Start with the main subject: "Two children playing", "A golden retriever", "Foosball figures on a table". Never start with "This" or "The image".
Count people: include the total in your opening (e.g. "Three people", "One child and one adult"). Use gender-neutral language: "person", "people", "child", "adult", never "man", "woman", "boy", "girl", "he", "she".
If you see a shape, silhouette, or backlit figure, only describe it as a person if you clearly see a face or human body. Otherwise, say "a shadow", "a silhouette", "a backlit shape".
Describe only what you see. If details are unclear, blurry, or ambiguous, omit them. Do not specify colors unless certain. Do not read small text on labels or packaging.
Write in prose, no lists or bullet points. Write in present tense.
```

### After (v15 - 268 words)
```
Describe this photo in 2-3 sentences for a searchable personal photo library.

MAIN SUBJECT: Start with the main subject: "Two children playing", "A golden retriever", "Foosball figures on a table". Never start with "This" or "The image".

PERSON COUNT (CRITICAL): Before writing, count every person visible in the frame, including background figures. Start your description with the exact count (e.g. "Three people", "One child and one adult", "A group of five"). Do NOT omit any people who occupy more than 5% of the frame. If person count is wrong, the entire description fails.

LANGUAGE: Use gender-neutral language only: "person", "people", "child", "adult". Never use "man", "woman", "boy", "girl", or gendered pronouns ("he", "she", "his", "her"). If gender is ambiguous, use "person" or "child".

SILHOUETTES & SHADOWS: If you see a shape, silhouette, or backlit figure, describe it as a person ONLY if you clearly see a face, eyes, or distinct human body shape. Otherwise describe as "a shadow", "a silhouette", or "a shape".

CLOTHING & DETAILS: Describe only clothing and accessories that are clearly and prominently visible. Do NOT infer, speculate, or describe details you cannot clearly see. If a garment, pattern, or accessory is partially obscured or too small to identify with certainty, omit it entirely rather than guess.

BLURRED, ROTATED & ANGLED IMAGES: If the image is blurry, motion-blurred, rotated, or subjects are viewed from behind/at angles, limit your description to what is clearly observable. State "partially visible" or "blurred" when image quality or positioning prevents confident description. Do NOT infer activities or emotions from posture if uncertain.

GENERAL RULES:
- Describe only what you clearly see. If details are unclear, ambiguous, or too small to read, omit them.
- Do not specify colors unless you are certain. Do not attempt to read small text on labels, signs, or packaging.
- Never repeat the same phrase or clause. If you find yourself repeating, stop immediately and move to the next detail.
- Write in prose, no lists or bullet points. Write in present tense.
```

---

## Key Improvements

### 1. **Person Count Made CRITICAL**

**Rationale:** Evaluation showed 72% error rate on person counting; entire descriptions failed when they omitted people.

**Change:**
- Mark "PERSON COUNT (CRITICAL)" section
- Add explicit "Do NOT omit any people who occupy more than 5% of the frame"
- State "If person count is wrong, the entire description fails"
- Require explicit count in opening sentence

**Impact:** Forces model to audit for all people before generating description

---

### 2. **Clothing & Details Hallucination Guard**

**Rationale:** G1 gate had only 64% pass rate due to invented details — "pearl necklace" not visible, "blue shirt" on costume character, specific colors on items too small to identify.

**Change:**
- New dedicated section: "CLOTHING & DETAILS"
- Ban speculation: "Do NOT infer, speculate, or describe details you cannot clearly see"
- Explicit instruction: "omit it entirely rather than guess"
- Threshold: "clearly and prominently visible"

**Impact:** Discourages model from filling in gaps with false details

---

### 3. **Blur/Rotation/Angle Guidance**

**Rationale:** Model frequently inverted view directions, invented activities, and misread postures when subjects were at angles or blurry.

**Change:**
- New section: "BLURRED, ROTATED & ANGLED IMAGES"
- Explicit guidance: "limit your description to what is clearly observable"
- Permission to state limitation: "State 'partially visible' or 'blurred' when..."
- Constraint on inference: "Do NOT infer activities or emotions from posture if uncertain"

**Impact:** Prevents confident-sounding false descriptions when image quality is poor

---

### 4. **Repetition Safeguard in Prompt**

**Rationale:** Photo 33 produced infinite loop ("long, and often, but not always, long, and often..."). While service has loop detection, prompt-level instruction can help prevent degeneration at source.

**Change:**
- Add to GENERAL RULES: "Never repeat the same phrase or clause. If you find yourself repeating, stop immediately and move to the next detail."

**Impact:** Primes model to self-interrupt if it detects repetition

---

### 5. **Reinforced Gender Neutrality**

**Rationale:** Existing guardrail was working (only rare misidentifications), but strengthen with explicit examples and constraint.

**Change:**
- New section: "LANGUAGE"
- List all gendered terms to avoid: "man", "woman", "boy", "girl", pronouns
- Clear fallback: "If gender is ambiguous, use 'person' or 'child'"

**Impact:** Reduces rare gender misidentifications; improves inclusivity

---

## Backend Improvements

### Enhanced Loop Detection (TextNormalizer.cs)

Added 3-word n-gram repetition check to catch stuttering patterns:

**Original (5-word only):**
```
"long, and often, but not always, long, and often..." — may not trigger on first repeat of 5-word pattern
```

**Enhanced (5-word + 3-word):**
```
- 5-word n-gram: triggers on 3+ occurrences (unchanged)
- 3-word n-gram: triggers on 4+ occurrences (new) — catches "long, and often, long, and often..."
```

**Impact:** More robust loop detection without breaking legitimate phrase reuse

---

## Prompt Version History

| Version | Date | Focus | Pass Rate |
|---------|------|-------|-----------|
| v5 | ~Mar 2026 | Evaluated in this session | 61% (6.5/9 avg) |
| ... | ... | Internal improvements | ... |
| v14 | Apr 18, 2026 | Pre-optimization | Unknown |
| v15 | Apr 19, 2026 | Eval-driven fixes | TBD (next run) |

---

## Testing & Deployment

### Build Status
✓ Clean build (0 errors, 1 pre-existing warning)
✓ All 486 tests pass
✓ No regressions

### Next Steps for Validation

**Immediate (manual):**
1. Test on a few problem photos from evaluation set (e.g., photos with multiple people, blurry images, rotated images)
2. Verify person counts are accurate on next reanalysis

**Medium-term (automated):**
1. Run evaluation harness on 100 new test photos with v15
2. Compare pass rate against v5 baseline (61%)
3. Track improvements by gate (especially G1 and G3)

**Long-term:**
1. Continuous monitoring of AnalysisMetrics table for description quality
2. A/B test with users if Prompt Harness indicates >70% pass rate

---

## Expected Improvements

Based on evaluation findings:

| Gate | Current (v5) | Expected (v15) | Target |
|------|--------------|----------------|--------|
| G1 (No Hallucination) | 64% | 75-80% | 90% |
| G2 (Subject Match) | 84% | 84% | 90% |
| G3 (Person Count) | 72% | 85-90% | 95% |
| G4 (Setting) | 81% | 81% | 90% |
| G5 (Key Object) | 79% | 85-90% | 95% |
| **Overall Pass Rate** | **61%** | **75-80%** | **90%+** |

**Confidence Level:** Medium-high
- Person count guardrail should directly improve G3 (72% → 90%+)
- Hallucination guard should improve G1 (64% → 75-80%)
- Blur/angle guidance should prevent some G2/G5 failures
- Conservative estimate: 61% → 75-80% overall

---

## Files Modified

| File | Changes |
|------|---------|
| `AppSettings.cs` | Version v14 → v15, prompt text expanded with sections |
| `TextNormalizer.cs` | Enhanced IsRepetitionLoop with 3-word n-gram check |

**Total changes:** 2 files, ~200 lines net addition (prompt expansion)

---

## Rollback Instructions

If v15 performs worse than v14 (unlikely but possible):

1. Change `CurrentPromptVersion = "v14"`
2. Revert `VisionAnalysisPrompt` to previous text
3. Revert TextNormalizer.cs IsRepetitionLoop to original 5-word only
4. Rebuild
5. Future descriptions will use v14 prompt

Photos analyzed with v15 will remain marked with PromptVersion = "v15" for historical tracking.

---

## Summary

**Prompt v15 implements all 5 evaluation-driven recommendations:**
1. ✓ Person count made mandatory and emphasized
2. ✓ Hallucination guardrails strengthened with explicit examples
3. ✓ Blur/angle handling guidance added
4. ✓ Repetition prevention instruction added at prompt level
5. ✓ Gender neutrality reinforced with examples

**Backend improvements:**
- ✓ Enhanced loop detection with 3-word n-gram check
- ✓ All tests passing (486/486)

**Expected outcome:** 61% pass rate → 75-80% pass rate (based on addressing known failure modes)

