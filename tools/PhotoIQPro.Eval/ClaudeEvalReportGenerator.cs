using System.Text;

namespace PhotoIQPro.Eval;

public static class ClaudeEvalReportGenerator
{
    public static string Generate(string runId, EvalDatabase db)
    {
        var rows   = db.GetClaudeEvalResults(runId);
        var rates  = db.GetClaudeGateRates(runId);

        var sb = new StringBuilder();

        sb.AppendLine($"# PhotoIQ — Claude Vision Eval Report");
        sb.AppendLine($"**Run:** {runId}  ");
        sb.AppendLine($"**Photos evaluated:** {rows.Count}  ");
        sb.AppendLine($"**Judge model:** Claude (claude-haiku-4-5)  ");
        sb.AppendLine($"**Gate scoring:** G1=3pts · G2=2pts · G3=1pt · G4=1pt · G5=2pts · Max=9  ");
        sb.AppendLine($"**Pass threshold:** ≥7/9 per photo  ");
        sb.AppendLine();

        // ── Gate summary ──────────────────────────────────────────────────────
        sb.AppendLine("## Gate Pass Rates");
        sb.AppendLine();
        sb.AppendLine("| Gate | Description | Rate | Status |");
        sb.AppendLine("|------|-------------|------|--------|");
        AppendGateRow(sb, "G1", "No Hallucination (3pts)", rates.G1);
        AppendGateRow(sb, "G2", "Subject Match (2pts)",    rates.G2);
        AppendGateRow(sb, "G3", "Person Count (1pt)",      rates.G3);
        AppendGateRow(sb, "G4", "Setting (1pt)",           rates.G4);
        AppendGateRow(sb, "G5", "Key Object (2pts)",       rates.G5);
        sb.AppendLine();

        double avgScore = rows.Count > 0 ? rows.Average(r => r.Score) : 0;
        int    passed   = rows.Count(r => r.Score >= 7);
        sb.AppendLine($"**Average score:** {avgScore:F1}/9  ");
        sb.AppendLine($"**Pass rate:** {passed}/{rows.Count} photos ≥ 7/9  ");
        sb.AppendLine();

        // ── Per-photo results ─────────────────────────────────────────────────
        sb.AppendLine("## Per-Photo Results");
        sb.AppendLine();
        sb.AppendLine("| Photo | G1 | G2 | G3 | G4 | G5 | Score | Verdict |");
        sb.AppendLine("|-------|----|----|----|----|----|-------|---------|");

        foreach (var r in rows.OrderByDescending(r => r.Score))
        {
            string G(bool v) => v ? "✓" : "✗";
            string g3 = r.PersonCountNull ? "–" : G(r.G3);
            string verdict = r.Score >= 7 ? "✓ PASS" : "✗ FAIL";
            sb.AppendLine($"| {r.FileName} | {G(r.G1)} | {G(r.G2)} | {g3} | {G(r.G4)} | {G(r.G5)} | {r.Score}/9 | {verdict} |");
        }

        sb.AppendLine();

        // ── Failures detail ───────────────────────────────────────────────────
        var failures = rows.Where(r => !r.G1 || !r.G2 || !r.G3 || !r.G4 || !r.G5).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("## Failures Detail");
            sb.AppendLine();
            foreach (var r in failures.OrderByDescending(r => r.Score))
            {
                sb.AppendLine($"### {r.FileName}  (score {r.Score}/9)");
                sb.AppendLine($"> {r.AiDescription}");
                sb.AppendLine();
                if (!r.G1) sb.AppendLine("- **G1 FAIL** — Hallucination detected");
                if (!r.G2) sb.AppendLine("- **G2 FAIL** — Main subject not correctly identified");
                if (!r.PersonCountNull && !r.G3) sb.AppendLine("- **G3 FAIL** — Person count incorrect");
                if (!r.G4) sb.AppendLine("- **G4 FAIL** — Setting incorrectly described");
                if (!r.G5) sb.AppendLine("- **G5 FAIL** — Key object not mentioned");
                if (!string.IsNullOrWhiteSpace(r.Notes))
                    sb.AppendLine($"- *Claude note:* {r.Notes}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void AppendGateRow(StringBuilder sb, string gate, string desc, double rate)
    {
        string status = rate >= 0.80 ? "✓ PASS" : rate >= 0.50 ? "⚠ WARN" : "✗ FAIL";
        sb.AppendLine($"| {gate} | {desc} | {rate:P0} | {status} |");
    }
}
