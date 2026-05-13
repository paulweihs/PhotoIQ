namespace PhotoWell.Eval;

/// <summary>
/// Orchestrates the Claude-as-judge eval loop:
///   1. Sample N photos from the app library.
///   2. For each, send the image + AiDescription to Claude for gate scoring.
///   3. Store results in claude_eval_results.
///   4. Print summary + save markdown report.
/// </summary>
public static class ClaudeEvalRunner
{
    // ms to wait between API calls — Haiku is fast but we respect rate limits
    private const int RateLimitDelayMs = 500;

    public static async Task<int> RunAsync(
        int    count,
        string evalDbPath,
        string reportDir,
        string claudeModel,
        string? libraryDbPath,
        string? promptVersion    = null,
        CancellationToken ct = default)
    {
        string runId = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

        Console.WriteLine($"Database: {evalDbPath}");
        Console.WriteLine($"Run ID:   {runId}");
        Console.WriteLine($"Model:    {claudeModel}");
        Console.WriteLine($"Sample:   {count} photos");
        if (promptVersion != null)
            Console.WriteLine($"Prompt:   {promptVersion} only");
        Console.WriteLine();

        // ── Sample photos ─────────────────────────────────────────────────────
        var photos = LibraryPhotoSampler.Sample(count, libraryDbPath, promptVersion);
        if (photos.Count == 0)
        {
            Console.WriteLine("[ERROR] No photos available to evaluate.");
            return 1;
        }

        // ── Prepare DB ────────────────────────────────────────────────────────
        using var db = new EvalDatabase(evalDbPath);
        db.EnsureSchema();
        db.EnsureClaudeEvalSchema();

        // ── Claude client ─────────────────────────────────────────────────────
        ClaudeClient claude;
        try
        {
            claude = new ClaudeClient(claudeModel);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        using (claude)
        {
            int passed = 0, failed = 0, skipped = 0;
            var scores = new List<int>(photos.Count);

            Console.WriteLine($"┌─ Claude eval ({photos.Count} photos)");

            foreach (var photo in photos)
            {
                if (ct.IsCancellationRequested) break;

                Console.WriteLine($"│");
                Console.WriteLine($"│  [{photo.FileName}]");
                Console.WriteLine($"│  App desc: \"{Truncate(photo.AiDescription, 100)}\"");

                ClaudeEvalResult? result = null;
                try
                {
                    result = await claude.EvaluateAsync(photo.ImagePath, photo.AiDescription, ct);
                    await Task.Delay(RateLimitDelayMs, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"│  [ERROR] {ex.GetType().Name}: {ex.Message}");
                    skipped++;
                    continue;
                }

                if (result == null)
                {
                    skipped++;
                    continue;
                }

                string G(bool v) => v ? "✓" : "✗";
                string G3 = result.PersonCount == null ? "–" : G(result.G3);
                Console.WriteLine(
                    $"│  Gates → " +
                    $"G1(no-hallucin):{G(result.G1)}  " +
                    $"G2(subject):{G(result.G2)}  " +
                    $"G3(persons):{G3}  " +
                    $"G4(setting):{G(result.G4)}  " +
                    $"G5(key-obj):{G(result.G5)}  " +
                    $"Score:{result.Score}/9");

                if (!string.IsNullOrWhiteSpace(result.Notes))
                    Console.WriteLine($"│  Notes: {result.Notes}");

                db.InsertClaudeEvalResult(runId, photo, result);
                scores.Add(result.Score);

                if (result.Score >= 7) passed++;
                else                   failed++;
            }

            Console.WriteLine($"└──────────────────────────────────");
            Console.WriteLine();

            // ── Summary ───────────────────────────────────────────────────────
            if (scores.Count > 0)
            {
                double avg = scores.Average();
                Console.WriteLine("═══════════════════════════════════════════════════════════");
                Console.WriteLine("  CLAUDE EVAL SUMMARY");
                Console.WriteLine("═══════════════════════════════════════════════════════════");
                Console.WriteLine($"  Photos evaluated : {scores.Count}");
                Console.WriteLine($"  Skipped          : {skipped}");
                Console.WriteLine($"  Pass (≥7/9)      : {passed}");
                Console.WriteLine($"  Fail (<7/9)      : {failed}");
                Console.WriteLine($"  Average score    : {avg:F1}/9");
                Console.WriteLine($"  Overall verdict  : {(avg >= 7.0 && failed == 0 ? "PASS" : "FAIL")}");
                Console.WriteLine("═══════════════════════════════════════════════════════════");

                // ── Gate breakdown ─────────────────────────────────────────────
                var gateRates = db.GetClaudeGateRates(runId);
                Console.WriteLine();
                Console.WriteLine("  Gate pass rates:");
                Console.WriteLine($"    G1 No Hallucination : {gateRates.G1:P0}  (3pts)");
                Console.WriteLine($"    G2 Subject Match    : {gateRates.G2:P0}  (2pts)");
                Console.WriteLine($"    G3 Person Count     : {gateRates.G3:P0}  (1pt, N/A excluded)");
                Console.WriteLine($"    G4 Setting          : {gateRates.G4:P0}  (1pt)");
                Console.WriteLine($"    G5 Key Object       : {gateRates.G5:P0}  (2pts)");
            }
        }

        // ── Markdown report ───────────────────────────────────────────────────
        Directory.CreateDirectory(reportDir);
        string safeRunId  = runId.Replace(":", "-");
        string reportPath = Path.Combine(reportDir, $"claude-eval-{safeRunId}.md");
        string report     = ClaudeEvalReportGenerator.Generate(runId, db);
        await File.WriteAllTextAsync(reportPath, report, ct);
        Console.WriteLine();
        Console.WriteLine($"Report saved: {reportPath}");

        return 0;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
