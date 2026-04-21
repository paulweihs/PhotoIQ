# Production Monitoring Setup — April 20, 2026

**Status:** READY FOR DEPLOYMENT
**Prompt Version:** v15c
**Database Indexes:** ✓ Deployed
**Gender Enforcement:** ✓ Active

---

## Monitoring Strategy

Three-tier approach for tracking vision description quality:

### Tier 1: Automatic Metrics (AnalysisMetrics table)

Currently tracked at import time:

```sql
SELECT
  PromptVersion,
  AiModelUsed,
  COUNT(*) as Count,
  AVG(DescriptionWordCount) as AvgWords,
  AVG(DescriptionCharCount) as AvgChars,
  SUM(CASE WHEN DescriptionEmpty THEN 1 ELSE 0 END) as EmptyCount,
  SUM(CASE WHEN LoopDetected THEN 1 ELSE 0 END) as LoopCount
FROM AnalysisMetrics
WHERE CreatedAt >= datetime('now', '-7 days')
GROUP BY PromptVersion, AiModelUsed
```

**What this tells us:**
- Word count trends (should be 40-100 words, not changing over time)
- Empty descriptions (should stay <5%)
- Loop detection hits (should be <1%)
- Performance by prompt version (v15c vs older)

**Implementation:** Already in place (OllamaVisionService calls RecordMetricsSafeAsync)

---

### Tier 2: Quarterly Evaluation (Manual)

Run evaluation harness every 3 months on fresh 50-image sample:

```bash
dotnet run --project tools/PhotoIQPro.Eval -- --optimize-prompt 50 --prompt "$PROMPT"
```

**Baseline established April 20, 2026:**
- Average Jaccard similarity: 0.223 (10 available images from 35 corpus)
- Target improvement: 10% per quarter (0.223 → 0.245 by Q3 2026)

**Gate breakdown (estimated from v15 evaluation):**
- G1 (Hallucinations): ~75% (improved from 64%)
- G2 (Subject match): ~85% (maintained from baseline)
- G3 (Person count): ~80% (improved from 72%)
- G4 (Relationships): ~80% (maintained)
- G5 (Determinism): ~95% (strong)
- G6 (Misclassification): ~95% (strong)

---

### Tier 3: User Feedback Loop (Ad-hoc)

Enable users to flag problematic descriptions:

1. **Right-click → Report Description** in photo details panel
2. Store in `DescriptionFeedback` table (new)
3. Review biweekly for patterns

**Schema suggestion:**
```sql
CREATE TABLE description_feedback (
  id INTEGER PRIMARY KEY,
  media_file_id TEXT NOT NULL,
  user_comment TEXT,
  categories TEXT,  -- JSON: ["hallucination", "gender", "clarity", "other"]
  created_at TEXT
)
```

---

## Key Metrics Dashboard

### Critical Alerts (trigger investigation)

1. **Loop detection spike:** >2% of descriptions flagged as loops
   - Suggests prompt is over-constraining model
   - Action: Rollback to v14, review v15c guidance

2. **Empty description rate:** >10% empty on any photo type
   - Suggests model confusion on specific image categories
   - Action: Analyze which photo types fail (faces, landscapes, etc.)

3. **Word count drift:** Mean description changes >20%
   - Suggests model behavior shift or Ollama version change
   - Action: Check Ollama version, compare with baseline

4. **Gender term reappearance:** >5% of descriptions contain gendered pronouns post-v15c
   - Suggests EnforceGenderNeutrality() not working
   - Action: Review TextNormalizer regex patterns

### Informational Metrics (track trends)

1. **Similarity score trend:** Track 7-day moving average
   - Expect stability (±0.02 variation normal)
   - Long-term trend >5% improvement = v16 candidates

2. **Analysis speed:** Track average inference time
   - Database indexes should reduce query overhead 5-10%
   - Model inference time consistent (~2-3s per photo)

3. **Determinism (G5):** %age of photos with identical descriptions across 3 passes
   - Target: >95% deterministic
   - Non-determinism acceptable for artistic/ambiguous photos

---

## SQL Monitoring Queries

### Daily Health Check
```sql
-- Check for anomalies in last 24 hours
SELECT
  PromptVersion,
  COUNT(*) as analyzed_count,
  SUM(CASE WHEN DescriptionEmpty THEN 1 ELSE 0 END) as empty_count,
  SUM(CASE WHEN LoopDetected THEN 1 ELSE 0 END) as loop_count,
  ROUND(100.0 * SUM(CASE WHEN LoopDetected THEN 1 ELSE 0 END) / COUNT(*), 2) as loop_pct,
  ROUND(AVG(DescriptionWordCount), 1) as avg_words
FROM AnalysisMetrics
WHERE CreatedAt >= datetime('now', '-1 day')
GROUP BY PromptVersion
```

Expected output (v15c):
```
v15c | 250 | 5 | 1 | 0.40 | 62.3
```

### Gender Enforcement Effectiveness
```sql
-- Count gendered nouns/pronouns in descriptions (post-v15c)
-- Regex: \b(man|woman|boy|girl|he|she|his|her|mother|father|actor|actress)\b
SELECT
  COUNT(*) as descriptions_with_gender_terms,
  ROUND(100.0 * COUNT(*) / (SELECT COUNT(*) FROM media_files WHERE prompt_version = 'v15c'), 2) as pct
FROM media_files
WHERE prompt_version = 'v15c'
  AND ai_description REGEXP '\\b(man|woman|boy|girl|he|she|his|her|mother|father|actor|actress)\\b'
```

Expected (should be <5%):
```
5 | 2.1
```

### Outdated Description Finder
```sql
-- Find photos needing reanalysis (uses (IsExcluded, IsAnalyzed, AiModelUsed) index)
SELECT COUNT(*)
FROM media_files
WHERE is_excluded = 0
  AND is_analyzed = 1
  AND ai_model_used != 'llama3.2-vision'
  AND prompt_version NOT IN ('v15c', 'v15b', 'v15a')
```

Expected perf:
- Without index: 150ms (table scan)
- With index: <5ms (index seek)

---

## Deployment Checklist

Before going live with v15c + indexes + gender enforcement:

- [x] v15c prompt deployed (CurrentPromptVersion = "v15c")
- [x] Database indexes created (PhotoIQContext.cs)
- [x] Gender enforcement active (OllamaVisionService)
- [x] All 486 tests passing
- [x] Build clean (0 errors)
- [x] Evaluation baseline established (0.223 avg similarity)
- [ ] First 100 descriptions analyzed and spot-checked for quality
- [ ] AnalysisMetrics dashboard available to monitor
- [ ] User feedback collection mechanism ready

---

## Schedule

### Week 1 (Apr 20-26)
- Deploy to production
- Monitor AnalysisMetrics for anomalies
- Daily health checks (loop detection, empty count, word count)

### Week 2-4 (Apr 27 - May 17)
- Collect feedback via description reports (if available)
- Spot-check 10-20 descriptions for hallucinations/gender issues
- Track similarity score on growing corpus

### Month 2 (May)
- Prepare quarterly evaluation on 50 new test photos
- Compare v15c performance vs v14 baseline
- Identify improvements and remaining issues

### Month 3+ (June onwards)
- Iterate on v16 based on v15c performance
- A/B test v16 on subset of library if improvements significant
- Monitor long-term trends (6+ month horizon)

---

## Rollback Plan

If v15c underperforms in production:

**Immediate rollback (2 minutes):**
```csharp
// AppSettings.cs
public const string CurrentPromptVersion = "v14";
public const string VisionAnalysisPrompt = /* v14 text */;

// OllamaVisionService.cs
// Comment out: var enforced = TextNormalizer.EnforceGenderNeutrality(neutralized);
// Use: return new ImageUnderstanding(neutralized, []);
```

**Then rebuild and redeploy.**

Photos analyzed with v15c remain marked with PromptVersion = "v15c" for historical tracking.

---

## Expected Outcomes

### Short-term (1 month)
- ✓ Stable description quality (0.22-0.24 similarity)
- ✓ <5% gendered terms (gender enforcement working)
- ✓ <1% loop detection (prompt not over-constraining)
- ✓ 10-15% faster reanalysis (indexes working)

### Medium-term (3 months)
- ✓ Evaluation shows >10% improvement vs v14 baseline
- ✓ User feedback identifies no critical issues
- ✓ Database indexes providing measurable speedup on large libraries
- ✓ Ready to plan v16 (minor refinements or new capabilities)

### Long-term (6+ months)
- ✓ v15c becomes standard across all users
- ✓ AnalysisMetrics database grows to 10K+ entries (statistically significant)
- ✓ Trends visible (model drift, Ollama version effects, etc.)
- ✓ Foundation for future prompt improvements (v16, v17, etc.)

---

## Contact & Escalation

**If alerts fire:**

1. **Loop detection spike** → Review TextNormalizer, check Ollama version
2. **Empty rate increase** → Sample affected photos, check for new image types
3. **Gender terms detected** → Verify EnforceGenderNeutrality() is active in OllamaVisionService
4. **Similarity score drops >10%** → Run evaluation harness, compare against baseline

Default: **Investigate before rollback.** Rollback only if cause not identified within 24 hours.

---

## Summary

**v15c production monitoring is:**
- ✓ Fully automated via AnalysisMetrics
- ✓ Quarterly validation via evaluation harness
- ✓ Optional user feedback collection
- ✓ Clear alerting thresholds
- ✓ Detailed SQL queries for investigation
- ✓ Fast rollback capability

**Ready for production deployment.**
