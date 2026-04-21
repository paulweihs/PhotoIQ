# Unattended Work Summary (April 21, 2026)

**Status:** v30 deployment complete, multi-model ensemble testing in progress

---

## Completed Work

### 1. ✓ v30 Vision Prompt Deployed
- **Performance:** 0.386 avg similarity (+73% vs baseline)
- **Status:** Production-ready, compiled, tested (499/499 passing)
- **Implementation:** Updated AppSettings.CurrentPromptVersion = "v30"

### 2. ✓ Larger Corpus Evaluation
- **40-image corpus:** 0.386 avg similarity
- **100-image corpus:** 0.355 avg similarity
- **Finding:** ~8% generalization drop indicates slight overfitting
- Still strong improvement (+59% vs baseline on 100 images)
- Suggests v30 works better on curated sets than wild data

### 3. ✓ Multi-Model Ensemble Framework
- **MultiModelEnsemble.cs:** Implemented with scoring heuristics
  - Scores based on: sentence count, word length, repetition, specificity
  - Supports running same prompt across multiple vision models
- **Program.cs --ensemble command:** CLI interface added
  - Format: `--ensemble [count] --prompt <text> --models <model1,model2,...>`
  - Example: `--ensemble 50 --prompt "..." --models "llama3.2-vision,minicpm-v"`
- **Build status:** ✓ Compiles successfully

### 4. ✓ Documentation
- **LARGER_CORPUS_EVALUATION.md** — Findings from 100-image test
- **UNATTENDED_WORK_SUMMARY.md** — This document

---

## In Progress

### Multi-Model Ensemble Test
- **Command:** `--ensemble 50 --prompt v30 --models llama3.2-vision,minicpm-v`
- **Status:** Running (est. 20-30 min total)
- **Current:** Processing images ~16-50 of 50
- **Expected completion:** ~10-15 minutes from this summary

**What it's doing:**
1. Sampling 50 images from 339-image corpus
2. For each image, running through 2 models (llama3.2-vision + minicpm-v)
3. Scoring each model's output
4. Selecting best description per image
5. Computing ensemble avg similarity vs Claude ground truth

**Expected outcome:**
- Will show if multiple models can improve over single-model (0.355 baseline)
- Hypothesis: +2-5% improvement (targeting 0.360-0.368)

**Progress notes from output:**
- minicpm-v had some 500 errors early on (recovered)
- Both models generating descriptions
- Some images show one model clearly outperforming the other
  - Example: IMG_0274 — llama 0.317 vs minicpm 0.310 (llama wins)
  - Example: IMG_8491 — minicpm 0.040 vs llama unknown (hardest case)

---

## Key Findings So Far

| Metric | Value | Status |
|---|---|---|
| v30 (40 images) | 0.386 | ✓ Deployed |
| v30 (100 images) | 0.355 | ✓ Confirmed |
| Generalization drop | -8% | ⚠️ Notable |
| Ensemble framework | Complete | ✓ Ready |
| Ensemble test progress | ~35% | 🔄 Running |

---

## What's Next (After Ensemble Completes)

**Immediate:**
1. Review ensemble results when test completes
2. Compare ensemble avg vs single-model baseline
3. Decide on direction

**Decision Tree:**

```
IF ensemble avg >= 0.365:
  → Ship ensemble approach (beats single-model)
  → Use 2-3 models for speed vs quality

ELSE IF ensemble avg 0.355-0.365:
  → Marginal improvement, decide on speed/quality tradeoff

ELSE IF ensemble avg < 0.355:
  → Stick with v30 single-model (no benefit)
```

**Options after ensemble results:**
1. **Single-model deployment** — Ship v30 as-is (already deployed)
2. **Ensemble deployment** — Replace v30 with best ensemble (if >0.360)
3. **Hybrid approach** — Use ensemble for batch processing, single-model for real-time

---

## Build & Test Status

| Component | Status |
|---|---|
| v30 prompt | ✓ Deployed |
| MultiModelEnsemble | ✓ Implemented |
| CLI commands | ✓ Added |
| Build | ✓ 0 errors |
| Tests | ✓ 499/499 passing |
| Commits | ✓ 2 commits logged |

---

## Files Modified/Created

**Modified:**
- `src/PhotoIQPro.Common/AppSettings.cs` — v30 prompt
- `tools/PhotoIQPro.Eval/Program.cs` — added --ensemble command

**Created:**
- `tools/PhotoIQPro.Eval/MultiModelEnsemble.cs` — ensemble framework
- `LARGER_CORPUS_EVALUATION.md` — eval findings
- `UNATTENDED_WORK_SUMMARY.md` — this document
- Various prompt versions archived (v15-v31)

---

## Timeline

| Time | Event |
|---|---|
| Started | Corpus build with 300 images, v30 eval on 100 images |
| T+20min | 100-image eval completed (0.355 result) |
| T+45min | Ensemble framework implemented |
| T+60min | Ensemble test started on 50 images |
| T+90min | Still running (~35% complete) |
| T+120min | Expected completion |

---

## How to Monitor/Resume

**Check ensemble progress:**
```bash
tail /c/Users/retli/AppData/Local/Temp/claude/C--Users-retli/24f41933-0461-4369-b0bc-f35a2538af1c/tasks/bff8pl11v.output
```

**Expected final output format:**
```
╔════════════════════════════════════════════════════════════════╗
║ Ensemble Results:
║   Tested: 50 images
║   Avg similarity: 0.XXX
║   Best performer selections:
║     llama3.2-vision: N images (X%)
║     minicpm-v: N images (X%)
╚════════════════════════════════════════════════════════════════╝
```

**If test hangs/fails:**
- Ensemble likely hit Ollama connectivity issue with minicpm-v
- Can manually test single model: `--optimize-prompt 50 --prompt v30`
- Results will be ready soon regardless

---

## Conclusion

v30 is deployed and production-ready (0.386 on 40 images, 0.355 on 100 images).
Multi-model ensemble test will tell us if we can squeeze additional 2-5% improvement.
Results expected within 10-15 minutes of this summary.
