# Larger Corpus Evaluation Results

**Date:** April 21, 2026
**Objective:** Validate v30 prompt generalization on larger/broader corpus

---

## Key Findings

### Generalization Drop Observed

| Corpus Size | Avg Similarity | Notes |
|---|---|---|
| **40 images** (initial) | 0.386 | Single corpus evaluation |
| **100 images** (broader) | 0.355 | -8% drop from 40-image baseline |
| **339 images** (full library) | TBD | In progress... |

### Interpretation

**v30 shows slight overfitting to initial 40-image test set:**
- Performance on 40-image corpus: 0.386 (+73% vs baseline)
- Performance on 100-image corpus: 0.355 (+59% vs baseline)
- Still strong improvement, but indicates prompt works better on smaller/curated sets

### Why the Drop?

1. **Diversity** — 100 images are more diverse than initial 40 (more edge cases)
2. **Larger corpus includes harder cases** — Initial 40 were somewhat cherry-picked
3. **Model consistency** — llama3.2-vision may be less consistent on wider variety

### Worst Cases on 100-Image Corpus

1. IMG_2043.JPG (0.053) — Fundamental mismatch
2. IMG_9856.JPG (0.056) — Likely multi-subject confusion
3. IMG_6917.JPG (0.087) — Edge case

---

## Multi-Model Ensemble: Next Step

**Hypothesis:** Using multiple vision models and selecting the best description per image could:
- Pick the strengths of each model (llama3.2-vision, minicpm-v, moondream, etc.)
- Average out individual model weaknesses
- Improve overall score by 2-5%

### Ensemble Strategy

1. Run each image through multiple Ollama models
2. Score descriptions based on:
   - Reasonable sentence count (2-3 sentences)
   - Appropriate word length (50-300 words)
   - Low repetition (penalize looping)
   - Absence of generic language
3. Select best-scoring description for each image
4. Compare ensemble result vs single-model baseline

### Available Models to Test

- `llama3.2-vision` — Current (llava-based, general purpose)
- `minicpm-v` — Smaller, faster (good for OCR too)
- `moondream` — Very small, specific for vision
- `bakllava` — Larger variant
- `llava:13b` — Larger llava version

---

## Implementation Status

**Ensemble Framework:** ✓ Implemented
- MultiModelEnsemble.cs — Multi-model runner with scoring heuristics
- Program.cs --ensemble command — CLI interface
- Build: ✓ Compiles successfully

**Next:** Test ensemble on 50-100 images to validate hypothesis

---

## Expected Timeline

1. **Ensemble test (50-100 images)** — 20-30 min depending on model count
2. **Compare to single-model baseline** — Direct similarity comparison
3. **Decide:** Keep single v30 or switch to ensemble approach

---

## Decision Framework

**If ensemble avg similarity ≥ 0.365:**
- ✓ Ship ensemble approach (beats single-model)
- Use 2-3 models for speed vs quality tradeoff

**If ensemble avg similarity < 0.355:**
- ✗ Stick with v30 single-model
- Further refinement likely not worth ROI

**If ensemble avg similarity 0.355-0.365:**
- ⚠ Marginal improvement
- Decide based on inference speed requirements
