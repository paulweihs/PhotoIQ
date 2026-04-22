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

## Completed (Latest)

### ✓ Multi-Model Ensemble Test (REJECTED)
- **Command:** `--ensemble 50 --prompt v30 --models llama3.2-vision,minicpm-v`
- **Status:** Completed with results
- **Result:** Ensemble approach **NOT recommended**

**Final Results:**
- Ensemble avg similarity: **0.152** (vs v30 baseline 0.355)
- llama3.2-vision selected: 40/50 (80%)
- minicpm-v selected: 10/50 (20%)
- Minicpm-v had 2 × 500 errors (Ollama connectivity)

**Key Finding:**
- Ensemble heuristics (sentence count, word length, repetition) do NOT correlate with Jaccard similarity
- Selecting "best" by heuristic rules actually degraded performance
- Single-model v30 is much better (0.355 baseline) than ensemble (0.152)

**Decision:** Stick with v30 single-model, no ensemble needed

---

## Key Findings So Far

| Metric | Value | Status |
|---|---|---|
| v30 (40 images) | 0.386 | ✓ Deployed |
| v30 (100 images) | 0.355 | ✓ Confirmed |
| Generalization drop | -8% | ⚠️ Notable but acceptable |
| Ensemble framework | Complete | ✓ Built |
| Ensemble test (50 images) | 0.152 | ❌ Rejected |

---

## Decision Made

**Ensemble avg: 0.152 << 0.355 baseline → REJECTED**

**Root Cause:** Ensemble scoring heuristics (optimized for description structure: sentence count, word length, repetition penalty) do NOT predict Jaccard similarity to ground truth. The "best" descriptions by heuristics were often the worst by actual ground truth alignment.

**Final Decision:** v30 single-model is production-ready and optimal.

**Next Steps:**
1. ✓ v30 deployed (CurrentPromptVersion = "v30")
2. ✓ Performance confirmed: 0.386 (40 images), 0.355 (100 images)
3. ✓ Ensemble evaluated and rejected
4. → Ready for production deployment
5. → Monitor real-world accuracy
6. → Future: Performance optimization (indexes, batch tuning) — see PERFORMANCE_OPTIMIZATION_GUIDE.md

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
