using System.Text;

namespace PhotoWell.Eval;

/// <summary>
/// Generates a markdown evaluation report for a single run, including gate pass
/// rates, per-photo results, and a regression/improvement diff vs the previous run.
/// </summary>
public static class MarkdownReportGenerator
{
    private static readonly string[] GateDescriptions =
    [
        "Object hallucination",
        "Activity inversion",
        "Filler / context tags",
        "Relationship inference",
        "Determinism (3-pass)",
        "Gesture misclassification",
    ];

    public static string Generate(
        string runId,
        EvalDatabase db,
        IReadOnlyList<string> models,
        IReadOnlyList<PhotoTestCase> testCases)
    {
        List<PhotoModelSummary> current  = db.GetRunSummary(runId);
        string? prevRunId                = db.GetPreviousRunId(runId);
        List<PhotoModelSummary>? prev    = prevRunId != null ? db.GetRunSummary(prevRunId) : null;

        var sb = new StringBuilder();

        // ── Header ──────────────────────────────────────────────────────────
        sb.AppendLine("# PhotoWell — Evaluation Report");
        sb.AppendLine();
        sb.AppendLine($"| | |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| **Run** | `{runId}` |");
        sb.AppendLine($"| **Models** | {string.Join(", ", models)} |");
        sb.AppendLine($"| **Test set** | {testCases.Count} photos |");
        sb.AppendLine($"| **Compared to** | {(prevRunId != null ? $"`{prevRunId}`" : "_(first run — no baseline)_")} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (current.Count == 0)
        {
            sb.AppendLine("_No results recorded for this run._");
            return sb.ToString();
        }

        // ── Gate Pass Rates ──────────────────────────────────────────────────
        sb.AppendLine("## Gate Pass Rates");
        sb.AppendLine();
        sb.AppendLine("Averaged across all photos and models in this run.");
        sb.AppendLine();
        sb.AppendLine("| Gate | Description | Pass Rate | Status |");
        sb.AppendLine("|------|-------------|-----------|--------|");

        double[] gateAvgs =
        [
            current.Average(s => s.G1),
            current.Average(s => s.G2),
            current.Average(s => s.G3),
            current.Average(s => s.G4),
            current.Average(s => s.G5),
            current.Average(s => s.G6),
        ];

        for (int i = 0; i < 6; i++)
        {
            double rate = gateAvgs[i];
            string status = rate >= 0.8 ? "PASS" : rate >= 0.5 ? "WARN" : "FAIL";
            sb.AppendLine($"| G{i + 1} | {GateDescriptions[i]} | {rate:P0} | {status} |");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Per-Photo Results ────────────────────────────────────────────────
        sb.AppendLine("## Per-Photo Results");
        sb.AppendLine();

        // Build header with one column per model
        var headerCols = new StringBuilder("| Photo |");
        var sepCols    = new StringBuilder("|-------|");
        foreach (string model in models)
        {
            string short_name = model.Length > 18 ? model[..18] : model;
            headerCols.Append($" {short_name} | Score |");
            sepCols.Append($"--------|-------|");
        }
        sb.AppendLine(headerCols.ToString());
        sb.AppendLine(sepCols.ToString());

        foreach (PhotoTestCase tc in testCases)
        {
            var row = new StringBuilder($"| {tc.FileName} |");
            foreach (string model in models)
            {
                PhotoModelSummary? s = current.FirstOrDefault(
                    r => r.Photo == tc.FileName && r.Model == model);

                if (s == null)
                {
                    row.Append(" — | — |");
                }
                else
                {
                    string gates = Gates(s);
                    string verdict = s.AvgScore >= 6.0 ? "PASS" : "FAIL";
                    row.Append($" {gates} | {s.AvgScore:F1}/9 {verdict} |");
                }
            }
            sb.AppendLine(row.ToString());
        }

        sb.AppendLine();
        sb.AppendLine("Gate columns: G1 G2 G3 G4 G5 G6 (✓ = pass across all 3 passes, ✗ = fail)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Regressions & Improvements ───────────────────────────────────────
        if (prev != null)
        {
            var regressions  = new List<string>();
            var improvements = new List<string>();

            foreach (var cur in current)
            {
                PhotoModelSummary? old = prev.FirstOrDefault(
                    p => p.Photo == cur.Photo && p.Model == cur.Model);
                if (old == null) continue;

                double[] curGates = [cur.G1, cur.G2, cur.G3, cur.G4, cur.G5, cur.G6];
                double[] oldGates = [old.G1, old.G2, old.G3, old.G4, old.G5, old.G6];

                for (int g = 0; g < 6; g++)
                {
                    bool curPass = curGates[g] > 0.5;
                    bool oldPass = oldGates[g] > 0.5;

                    if (oldPass && !curPass)
                        regressions.Add($"| {cur.Photo} | {cur.Model} | G{g + 1} — {GateDescriptions[g]} | ✓ → ✗ |");
                    else if (!oldPass && curPass)
                        improvements.Add($"| {cur.Photo} | {cur.Model} | G{g + 1} — {GateDescriptions[g]} | ✗ → ✓ |");
                }
            }

            sb.AppendLine($"## Regressions vs `{prevRunId}`");
            sb.AppendLine();
            if (regressions.Count == 0)
            {
                sb.AppendLine("_No regressions detected._");
            }
            else
            {
                sb.AppendLine("| Photo | Model | Gate | Change |");
                sb.AppendLine("|-------|-------|------|--------|");
                regressions.ForEach(r => sb.AppendLine(r));
            }

            sb.AppendLine();
            sb.AppendLine($"## Improvements vs `{prevRunId}`");
            sb.AppendLine();
            if (improvements.Count == 0)
            {
                sb.AppendLine("_No improvements detected._");
            }
            else
            {
                sb.AppendLine("| Photo | Model | Gate | Change |");
                sb.AppendLine("|-------|-------|------|--------|");
                improvements.ForEach(i => sb.AppendLine(i));
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // ── Common Failures ──────────────────────────────────────────────────
        sb.AppendLine("## Common Failures");
        sb.AppendLine();

        int total = current.Count;
        var failureCounts = new[]
        {
            (Gate: "G1", Desc: GateDescriptions[0], Count: current.Count(s => s.G1 <= 0.5)),
            (Gate: "G2", Desc: GateDescriptions[1], Count: current.Count(s => s.G2 <= 0.5)),
            (Gate: "G3", Desc: GateDescriptions[2], Count: current.Count(s => s.G3 <= 0.5)),
            (Gate: "G4", Desc: GateDescriptions[3], Count: current.Count(s => s.G4 <= 0.5)),
            (Gate: "G5", Desc: GateDescriptions[4], Count: current.Count(s => s.G5 <= 0.5)),
            (Gate: "G6", Desc: GateDescriptions[5], Count: current.Count(s => s.G6 <= 0.5)),
        };

        var failures = failureCounts.Where(f => f.Count > 0)
                                    .OrderByDescending(f => f.Count)
                                    .ToList();

        if (failures.Count == 0)
        {
            sb.AppendLine("_All gates passed on all photos._");
        }
        else
        {
            sb.AppendLine("| Gate | Description | Failures | Rate |");
            sb.AppendLine("|------|-------------|----------|------|");
            foreach (var f in failures)
                sb.AppendLine($"| {f.Gate} | {f.Desc} | {f.Count}/{total} | {(double)f.Count / total:P0} |");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    // Renders a compact G1..G6 gate string: "✓✓✗✓✓✓"
    private static string Gates(PhotoModelSummary s)
    {
        static string P(double v) => v > 0.5 ? "✓" : "✗";
        return $"{P(s.G1)}{P(s.G2)}{P(s.G3)}{P(s.G4)}{P(s.G5)}{P(s.G6)}";
    }
}
