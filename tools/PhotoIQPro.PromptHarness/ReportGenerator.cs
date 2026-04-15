using System.Text;

namespace PhotoIQPro.PromptHarness;

public static class ReportGenerator
{
    public static string Generate(
        EvalRun run,
        List<EvalResult> results,
        GatesConfig gates,
        PromptRecord prompt,
        PromptRecord? parent,
        List<EvalRun>? parentRuns)
    {
        var sb = new StringBuilder();
        double passPct = run.ImagesEvaluated > 0
            ? (double)run.PassCount / run.ImagesEvaluated * 100 : 0;

        sb.AppendLine($"# Prompt Harness Report — {run.RunType.ToUpper()} EVAL");
        sb.AppendLine();
        sb.AppendLine($"| | |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Run ID | `{run.RunId}` |");
        sb.AppendLine($"| Prompt | `{run.PromptVersion}` |");
        sb.AppendLine($"| Parent | `{prompt.ParentVersion ?? "—"}` |");
        sb.AppendLine($"| Model | `{run.OllamaModel}` |");
        sb.AppendLine($"| Type | {run.RunType} |");
        sb.AppendLine($"| Date | {run.StartedAt[..10]} |");
        sb.AppendLine();

        // ── Score summary ──────────────────────────────────────────────────────
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Evaluated | {run.ImagesEvaluated} |");
        sb.AppendLine($"| Skipped (offline) | {run.ImagesSkipped} |");
        sb.AppendLine($"| Pass (≥{gates.PassThresholdPoints}pts) | {run.PassCount} ({passPct:F0}%) |");
        sb.AppendLine($"| Fail | {run.FailCount} |");
        sb.AppendLine($"| Avg score | {run.AvgScore:F1} / {run.AvgMaxScore:F1} |");
        sb.AppendLine($"| **Verdict** | **{(passPct >= 80 ? "PASS ✓" : "FAIL ✗")}** |");
        sb.AppendLine();

        // ── Regression comparison ──────────────────────────────────────────────
        if (parent != null && parentRuns?.Count > 0)
        {
            var bestParent = parentRuns
                .Where(r => r.RunType == run.RunType && r.ImagesEvaluated > 0)
                .OrderByDescending(r => r.AvgScore)
                .FirstOrDefault();

            if (bestParent != null)
            {
                double delta = run.AvgScore - bestParent.AvgScore;
                string arrow = delta >= 0 ? "▲" : "▼";
                string color = delta >= 0 ? "improvement" : "regression";
                sb.AppendLine("## vs Parent Prompt");
                sb.AppendLine();
                sb.AppendLine($"| | `{run.PromptVersion}` | `{parent.Version}` | Δ |");
                sb.AppendLine($"|---|---|---|---|");
                sb.AppendLine($"| Avg score | {run.AvgScore:F1} | {bestParent.AvgScore:F1} | {arrow} {Math.Abs(delta):F1} ({color}) |");
                double parentPct = bestParent.ImagesEvaluated > 0
                    ? (double)bestParent.PassCount / bestParent.ImagesEvaluated * 100 : 0;
                sb.AppendLine($"| Pass rate | {passPct:F0}% | {parentPct:F0}% | — |");
                sb.AppendLine();
            }
        }

        // ── Gate pass rates ────────────────────────────────────────────────────
        sb.AppendLine("## Gate Pass Rates");
        sb.AppendLine();
        sb.AppendLine("| Gate | Name | Points | Pass | Rate |");
        sb.AppendLine("|------|------|--------|------|------|");

        var gateRates = new Dictionary<string, (int passed, int total)>();
        foreach (var r in results)
            foreach (var (gateId, score) in r.GateScores)
            {
                gateRates.TryGetValue(gateId, out var cur);
                gateRates[gateId] = (score > 0 ? cur.Item1 + 1 : cur.Item1, cur.Item2 + 1);
            }

        foreach (var gate in gates.Gates)
        {
            if (!gateRates.TryGetValue(gate.Id, out var rate)) continue;
            double pct = rate.total > 0 ? (double)rate.passed / rate.total * 100 : 0;
            string bar = PctBar(pct);
            sb.AppendLine($"| {gate.Id} | {gate.Name} | {gate.Points} | {rate.passed}/{rate.total} | {bar} {pct:F0}% |");
        }
        sb.AppendLine();

        // ── Failures ───────────────────────────────────────────────────────────
        var failures = results.Where(r => !r.Passed).OrderBy(r => r.TotalScore).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("## Failures");
            sb.AppendLine();
            sb.AppendLine("| File | Score | Failed Gates | Notes |");
            sb.AppendLine("|------|-------|--------------|-------|");
            foreach (var f in failures)
            {
                var failedGates = f.GateScores
                    .Where(kv => kv.Value == 0)
                    .Select(kv => kv.Key)
                    .ToList();
                string gateStr  = string.Join(", ", failedGates);
                string notesStr = f.Notes?.Replace("|", "\\|") ?? "";
                sb.AppendLine($"| {f.FileName} | {f.TotalScore}/{f.MaxScore} | {gateStr} | {notesStr} |");
            }
            sb.AppendLine();
        }

        // ── Prompt text ────────────────────────────────────────────────────────
        sb.AppendLine("## Prompt");
        sb.AppendLine();
        sb.AppendLine($"**Version:** `{prompt.Version}`");
        if (prompt.Notes != null) sb.AppendLine($"**Notes:** {prompt.Notes}");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(prompt.Text);
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static string PctBar(double pct)
    {
        int filled = (int)(pct / 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }
}
