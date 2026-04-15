using PhotoIQPro.Eval;

// ── Argument parsing ──────────────────────────────────────────────────────────
if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  PhotoIQPro.Eval <testset-folder> [model1 model2 ...]");
    Console.WriteLine("    testset-folder  Path to directory containing the test JPG files.");
    Console.WriteLine("    model1 ...      Optional: Ollama model names. Defaults to: " +
                      string.Join(", ", TestConfig.DefaultModels));
    Console.WriteLine();
    Console.WriteLine("  PhotoIQPro.Eval --claude-eval [count] [--model claude-haiku-4-5-20251001] [--db <path>]");
    Console.WriteLine("    count           Number of library photos to evaluate (default: 20).");
    Console.WriteLine("    --model         Claude model ID (default: claude-haiku-4-5-20251001).");
    Console.WriteLine("    --db            Path to photoiq.db (default: %LOCALAPPDATA%\\PhotoIQPro\\photoiq.db).");
    Console.WriteLine("    Requires: ANTHROPIC_API_KEY environment variable.");
    return 1;
}

// ── Dump sample mode (used by Claude Code vision eval) ────────────────────────
if (args[0] == "--dump-sample")
{
    int count = 100;
    string? libraryDb = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--db" && i + 1 < args.Length) libraryDb = args[++i];
        else if (int.TryParse(args[i], out int n))     count     = n;
    }
    var photos = LibraryPhotoSampler.Sample(count, libraryDb);
    foreach (var p in photos)
        Console.WriteLine($"{p.ImagePath}|||{p.FileName}|||{p.AiDescription.Replace('\n',' ').Replace('\r',' ')}");
    return 0;
}

// ── Claude eval mode ──────────────────────────────────────────────────────────
if (args[0] == "--claude-eval")
{
    int    count         = 20;
    string claudeModel   = "claude-haiku-4-5-20251001";
    string? libraryDb    = null;
    string? promptFilter = null;

    for (int i = 1; i < args.Length; i++)
    {
        if      (args[i] == "--model"  && i + 1 < args.Length) { claudeModel   = args[++i]; }
        else if (args[i] == "--db"     && i + 1 < args.Length) { libraryDb     = args[++i]; }
        else if (args[i] == "--prompt" && i + 1 < args.Length) { promptFilter  = args[++i]; }
        else if (int.TryParse(args[i], out int n))              { count         = n; }
    }

    string evalDbPath      = Path.Combine(Directory.GetCurrentDirectory(), "evaluations.db");
    string claudeReportDir = @"C:\Dev\PhotoIQPro\eval";

    return await ClaudeEvalRunner.RunAsync(count, evalDbPath, claudeReportDir, claudeModel, libraryDb, promptFilter);
}

string testsetFolder = args[0];
string[] models = args.Length > 1
    ? args[1..]
    : TestConfig.DefaultModels;

if (!Directory.Exists(testsetFolder))
{
    Console.WriteLine($"Error: testset folder not found: {testsetFolder}");
    return 1;
}

// ── Setup ─────────────────────────────────────────────────────────────────────
string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "evaluations.db");
string runId  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

Console.WriteLine($"Database: {dbPath}");
Console.WriteLine($"Run ID:   {runId}");
Console.WriteLine($"Testset:  {testsetFolder}");
Console.WriteLine($"Models:   {string.Join(", ", models)}");
Console.WriteLine();

using var db = new EvalDatabase(dbPath);
db.EnsureSchema();
db.SeedEvaluationSet(TestConfig.Photos);
PhotoTestCase[] testCases = db.GetTestCases();

using var ollama = new OllamaRunner();

// ── Model loop ────────────────────────────────────────────────────────────────
foreach (string model in models)
{
    Console.WriteLine($"┌─ Model: {model}");

    // Check Ollama availability before spending time on the test set.
    bool available = await ollama.IsModelAvailableAsync(model);
    if (!available)
    {
        Console.WriteLine($"│  [SKIP] Model '{model}' is not available in Ollama. Run: ollama pull {model}");
        Console.WriteLine($"└──────────────────────────────────");
        Console.WriteLine();
        continue;
    }

    // ── Photo loop ────────────────────────────────────────────────────────────
    foreach (PhotoTestCase testCase in testCases)
    {
        string imagePath = Path.Combine(testsetFolder, testCase.FileName);

        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"│  [SKIP] File not found: {imagePath}");
            continue;
        }

        Console.WriteLine($"│");
        Console.WriteLine($"│  Testing [{model}] on [{testCase.FileName}]...");

        // We run 3 passes to evaluate Gate 5 (determinism).
        var passResults = new List<(bool g1, bool g2, bool g3, bool g4, bool g6)>(3);
        var rowIds      = new List<long>(3);

        for (int pass = 1; pass <= 3; pass++)
        {
            Console.Write($"│    Pass {pass}/3  → ");

            string description = await ollama.GenerateAsync(model, TestConfig.Prompt, imagePath);

            // Evaluate all per-pass gates (G5 is computed after all 3 passes).
            bool g1 = GateEvaluator.Gate1(description, testCase.Gate1Forbidden);
            bool g2 = GateEvaluator.Gate2(description, testCase.Gate2Forbidden);
            bool g3 = GateEvaluator.Gate3(description);
            bool g4 = GateEvaluator.Gate4(description);
            bool g6 = GateEvaluator.Gate6(description, testCase.HasGesture, testCase.Gate6ForbiddenMisclassifications);

            // Preliminary score without G5 (gate_5 will be back-filled below).
            // We pass g5=false as placeholder — UpdateGate5AndScore will correct this.
            int prelimScore = GateEvaluator.Score(g1, g2, g3, g4, g5: false, g6);

            long rowId = db.InsertResult(
                runId, model, testCase.FileName, pass, description,
                g1, g2, g3, g4, g6, prelimScore);

            rowIds.Add(rowId);
            passResults.Add((g1, g2, g3, g4, g6));

            // Print inline gate results. G5 is not yet known.
            string G(bool v) => v ? "✓" : "✗";
            Console.WriteLine($"[G1:{G(g1)} G2:{G(g2)} G3:{G(g3)} G4:{G(g4)} G5:? G6:{G(g6)}]");

            // Print the description in verbose mode.
            if (!string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine($"│           \"{description.Trim()}\"");
            }
            else
            {
                Console.WriteLine("│           (empty response)");
            }
        }

        // ── Gate 5: determinism across all 3 passes ───────────────────────────
        bool g5 = GateEvaluator.Gate5(passResults);

        Console.WriteLine($"│    → {(g5 ? "[G5:✓ DETERMINISTIC]" : "[G5:✗ NON-DETERMINISTIC]")}");

        // Back-fill Gate 5 and recompute the final score for all 3 stored rows.
        for (int i = 0; i < 3; i++)
        {
            var (g1, g2, g3, g4, g6) = passResults[i];
            int finalScore = GateEvaluator.Score(g1, g2, g3, g4, g5, g6);
            db.UpdateGate5AndScore(rowIds[i], g5, finalScore);
        }
    }

    Console.WriteLine($"└──────────────────────────────────");
    Console.WriteLine();
}

// ── Print full summary ────────────────────────────────────────────────────────
var photoNames = testCases.Select(p => p.FileName).ToList();
db.PrintSummary(runId, models, photoNames);

// ── Generate and save markdown report ─────────────────────────────────────────
string reportDir = @"C:\Dev\PhotoIQPro\eval";
Directory.CreateDirectory(reportDir);
string safeRunId  = runId.Replace(":", "-");
string reportPath = Path.Combine(reportDir, $"eval-{safeRunId}.md");
string report = MarkdownReportGenerator.Generate(runId, db, models, testCases);
await File.WriteAllTextAsync(reportPath, report);
Console.WriteLine();
Console.WriteLine($"Report saved: {reportPath}");

return 0;
