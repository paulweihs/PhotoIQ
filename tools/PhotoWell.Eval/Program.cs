using PhotoWell.Eval;

// ── Argument parsing ──────────────────────────────────────────────────────────
if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  PhotoWell.Eval <testset-folder> [model1 model2 ...]");
    Console.WriteLine("    testset-folder  Path to directory containing the test JPG files.");
    Console.WriteLine("    model1 ...      Optional: Ollama model names. Defaults to: " +
                      string.Join(", ", TestConfig.DefaultModels));
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --claude-eval [count] [--model claude-haiku-4-5-20251001] [--db <path>]");
    Console.WriteLine("    count           Number of library photos to evaluate (default: 20).");
    Console.WriteLine("    --model         Claude model ID (default: claude-haiku-4-5-20251001).");
    Console.WriteLine("    --db            Path to photowell.db (default: %LOCALAPPDATA%\\PhotoWell\\photowell.db).");
    Console.WriteLine("    Requires: ANTHROPIC_API_KEY environment variable.");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --corpus-status [--eval-db <path>]");
    Console.WriteLine("    Shows how many images are in the reference corpus, broken down by decade.");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --rebuild-corpus [--eval-db <path>] [--main-db <path>]");
    Console.WriteLine("    Validates and cleans up the reference corpus:");
    Console.WriteLine("    - Deletes orphaned entries (no source file)");
    Console.WriteLine("    - Validates remaining entries have both source and image files");
    Console.WriteLine("    --eval-db       Path to evaluations.db (default: ./evaluations.db)");
    Console.WriteLine("    --main-db       Path to photowell.db (default: %LOCALAPPDATA%\\PhotoWell\\photowell.db)");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --build-corpus [count] [--eval-db <path>] [--main-db <path>]");
    Console.WriteLine("    Rebuilds ground truth corpus by sampling library photos with existing");
    Console.WriteLine("    Claude-generated descriptions (no API calls required).");
    Console.WriteLine("    count           Number of photos to sample (default: 40)");
    Console.WriteLine("    --eval-db       Path to evaluations.db (default: ./evaluations.db)");
    Console.WriteLine("    --main-db       Path to photowell.db (default: %LOCALAPPDATA%\\PhotoWell\\photowell.db)");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --list-unprocessed [count] [--db <path>] [--eval-db <path>]");
    Console.WriteLine("    count           Number of images to list (default: 50).");
    Console.WriteLine("    --db            Path to photowell.db.");
    Console.WriteLine("    --eval-db       Path to evaluations.db (default: ./evaluations.db).");
    Console.WriteLine("    Outputs one line per image: imagePath|||fileName|||filePath|||year");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --ingest-description --image <path> --file <path> --name <name> --desc-file <path> [--eval-db <path>] [--year <yyyy>]");
    Console.WriteLine("    Reads a Claude-generated description from --desc-file and persists it to the corpus.");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --optimize-prompt [count] --prompt <text> [--model llama3.2-vision] [--db <path>] [--eval-db <path>]");
    Console.WriteLine("    count           Number of images to sample (default: 300).");
    Console.WriteLine("    --prompt        The vision prompt to test (required).");
    Console.WriteLine("    --model         Ollama model (default: llama3.2-vision).");
    Console.WriteLine("    --db            Path to photowell.db.");
    Console.WriteLine("    --eval-db       Path to evaluations.db (default: ./evaluations.db).");
    Console.WriteLine();
    Console.WriteLine("  PhotoWell.Eval --ensemble [count] --prompt <text> --models <model1,model2,...> [--eval-db <path>]");
    Console.WriteLine("    Test prompt across multiple vision models, select best result for each image.");
    Console.WriteLine("    count           Number of images to sample (default: 50).");
    Console.WriteLine("    --prompt        The vision prompt to test (required).");
    Console.WriteLine("    --models        Comma-separated Ollama models (e.g., llama3.2-vision,minicpm-v,moondream).");
    Console.WriteLine("    --eval-db       Path to evaluations.db (default: ./evaluations.db).");
    return 1;
}

// ── Insert ground truth descriptions into corpus ────────────────────────────────
if (args[0] == "--insert-ground-truth")
{
    if (args.Length < 2)
    {
        Console.WriteLine("[ERROR] --insert-ground-truth requires --desc-file or inline descriptions");
        return 1;
    }

    // Parse: --insert-ground-truth file.json [--eval-db path]
    string? descFile = null;
    string evalDb = "evaluations.db";

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--desc-file" && i + 1 < args.Length)
            descFile = args[++i];
        else if (args[i] == "--eval-db" && i + 1 < args.Length)
            evalDb = args[++i];
    }

    if (string.IsNullOrWhiteSpace(descFile) || !File.Exists(descFile))
    {
        Console.WriteLine("[ERROR] Description file not found");
        return 1;
    }

    // Parse JSON with descriptions
    using var gtDb = new EvalDatabase(evalDb);
    gtDb.EnsureCorpusSchema();

    string json = await File.ReadAllTextAsync(descFile);
    var records = System.Text.Json.JsonSerializer.Deserialize<List<GroundTruthRecord>>(json);

    if (records == null || records.Count == 0)
    {
        Console.WriteLine("[ERROR] No records in file");
        return 1;
    }

    foreach (var rec in records)
    {
        gtDb.InsertCorpusEntry(rec.FileName, rec.FilePath, rec.ImagePath, rec.PhotoYear, rec.Description, "claude-code");
        Console.WriteLine($"[OK] {rec.FileName}");
    }

    Console.WriteLine($"\n[OK] Inserted {records.Count} ground truth descriptions");
    return 0;
}

// ── Sample images for manual Claude Code analysis ──────────────────────────────
if (args[0] == "--sample-for-analysis")
{
    int count = 300;
    string? libraryDb = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--db" && i + 1 < args.Length)
            libraryDb = args[++i];
        else if (int.TryParse(args[i], out int n))
            count = n;
    }

    libraryDb ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoWell", "photowell.db");

    var photos = LibraryPhotoSampler.Sample(count, libraryDb);
    if (photos.Count == 0)
    {
        Console.WriteLine("[ERROR] No photos found.");
        return 1;
    }

    // Output paths for Claude Code to analyze
    foreach (var p in photos)
        Console.WriteLine(p.ImagePath);

    Console.WriteLine($"\n[OK] {photos.Count} images ready for analysis", System.Console.Error);
    return 0;
}

// ── Corpus status mode ────────────────────────────────────────────────────────
if (args[0] == "--corpus-status")
{
    string evalDb = "evaluations.db";
    for (int i = 1; i < args.Length; i++)
        if (args[i] == "--eval-db" && i + 1 < args.Length) evalDb = args[++i];

    using var statusDb = new EvalDatabase(evalDb);
    statusDb.EnsureCorpusSchema();
    var (total, byDecade) = statusDb.GetCorpusStats();

    Console.WriteLine($"Reference corpus: {total} images");
    if (byDecade.Count > 0)
    {
        Console.WriteLine("  By decade:");
        foreach (var (decade, count) in byDecade)
            Console.WriteLine($"    {decade}s : {count}");
    }
    return 0;
}

// ── Rebuild corpus mode ───────────────────────────────────────────────────────
if (args[0] == "--rebuild-corpus")
{
    string evalDb = "evaluations.db";
    string? mainDb = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--eval-db" && i + 1 < args.Length)
            evalDb = args[++i];
        else if (args[i] == "--main-db" && i + 1 < args.Length)
            mainDb = args[++i];
    }

    mainDb ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoWell", "photowell.db");

    var rebuild = new CorpusRebuild(evalDb, mainDb);
    var result = await rebuild.RunAsync();

    Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║ Corpus Rebuild Summary");
    Console.WriteLine("├────────────────────────────────────────────────────────────────┤");
    Console.WriteLine($"║ Total entries at start: {result.TotalEntries}");
    Console.WriteLine($"║ Valid entries: {result.ValidEntries}");
    Console.WriteLine($"║ Missing source: {result.MissingSourceCount}");
    Console.WriteLine($"║ Missing image: {result.MissingImageCount}");
    Console.WriteLine($"║ Orphaned entries deleted: {result.DeletedCount}");
    Console.WriteLine($"║ Final valid entries: {result.FinalValidCount}");
    Console.WriteLine($"║ Status: {(result.Success ? "✓ PASS" : "⚠ INCOMPLETE")}");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");

    return result.Success ? 0 : 1;
}

// ── Build corpus mode ─────────────────────────────────────────────────────────
if (args[0] == "--build-corpus")
{
    int count = 40;
    string evalDb = "evaluations.db";
    string? mainDb = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--eval-db" && i + 1 < args.Length)
            evalDb = args[++i];
        else if (args[i] == "--main-db" && i + 1 < args.Length)
            mainDb = args[++i];
        else if (int.TryParse(args[i], out int n))
            count = n;
    }

    mainDb ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoWell", "photowell.db");

    var builder = new CorpusBuilder(evalDb, mainDb);
    var result = await builder.RunAsync(count);

    Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║ Corpus Build Summary");
    Console.WriteLine("├────────────────────────────────────────────────────────────────┤");
    Console.WriteLine($"║ Photos sampled: {result.SampledCount}");
    Console.WriteLine($"║ Descriptions generated: {result.DescriptionsGenerated}");
    Console.WriteLine($"║ Entries inserted: {result.InsertedCount}");
    Console.WriteLine($"║ Status: {(result.Success ? "✓ SUCCESS" : "⚠ PARTIAL")}");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");

    return result.Success ? 0 : 1;
}

// ── List unprocessed mode ─────────────────────────────────────────────────────
if (args[0] == "--list-unprocessed")
{
    int     count      = 50;
    string? libraryDb  = null;
    string  evalDb     = "evaluations.db";

    for (int i = 1; i < args.Length; i++)
    {
        if      (args[i] == "--db"      && i + 1 < args.Length) libraryDb = args[++i];
        else if (args[i] == "--eval-db" && i + 1 < args.Length) evalDb    = args[++i];
        else if (int.TryParse(args[i], out int n))               count     = n;
    }

    using var listDb = new EvalDatabase(evalDb);
    listDb.EnsureCorpusSchema();
    var alreadyDone = listDb.GetCorpusFileNames();

    var photos = CorpusSampler.Sample(count, libraryDb, alreadyDone);
    foreach (var p in photos)
        Console.WriteLine($"{p.ImagePath}|||{p.FileName}|||{p.FilePath}|||{p.PhotoYear?.ToString() ?? "unknown"}");

    return 0;
}

// ── Ingest description mode ───────────────────────────────────────────────────
if (args[0] == "--ingest-description")
{
    string? imagePath = null, filePath = null, fileName = null, descFile = null, evalDb = null;
    int?    year      = null;

    for (int i = 1; i < args.Length; i++)
    {
        if      (args[i] == "--image"    && i + 1 < args.Length) imagePath = args[++i];
        else if (args[i] == "--file"     && i + 1 < args.Length) filePath  = args[++i];
        else if (args[i] == "--name"     && i + 1 < args.Length) fileName  = args[++i];
        else if (args[i] == "--desc-file"&& i + 1 < args.Length) descFile  = args[++i];
        else if (args[i] == "--eval-db"  && i + 1 < args.Length) evalDb    = args[++i];
        else if (args[i] == "--year"     && i + 1 < args.Length && int.TryParse(args[++i], out int y)) year = y;
    }

    if (imagePath == null || filePath == null || fileName == null || descFile == null)
    {
        Console.WriteLine("[ERROR] --ingest-description requires --image, --file, --name, and --desc-file.");
        return 1;
    }

    if (!File.Exists(descFile))
    {
        Console.WriteLine($"[ERROR] Description file not found: {descFile}");
        return 1;
    }

    string description = (await File.ReadAllTextAsync(descFile)).Trim();
    if (string.IsNullOrWhiteSpace(description))
    {
        Console.WriteLine("[ERROR] Description file is empty.");
        return 1;
    }

    evalDb ??= "evaluations.db";
    using var ingestDb = new EvalDatabase(evalDb);
    ingestDb.EnsureCorpusSchema();

    bool inserted = ingestDb.InsertCorpusEntry(fileName, filePath, imagePath, year, description);
    Console.WriteLine(inserted
        ? $"[OK] Ingested: {fileName}"
        : $"[SKIP] Already in corpus: {fileName}");

    return 0;
}

// ── Prompt optimization mode ──────────────────────────────────────────────────
if (args[0] == "--optimize-prompt")
{
    int     count      = 300;
    string? prompt     = null;
    string  model      = "llama3.2-vision";
    string? libraryDb  = null;
    string  evalDb     = "evaluations.db";

    for (int i = 1; i < args.Length; i++)
    {
        if      (args[i] == "--prompt" && i + 1 < args.Length) prompt    = args[++i];
        else if (args[i] == "--model"  && i + 1 < args.Length) model     = args[++i];
        else if (args[i] == "--db"     && i + 1 < args.Length) libraryDb = args[++i];
        else if (args[i] == "--eval-db"&& i + 1 < args.Length) evalDb    = args[++i];
        else if (int.TryParse(args[i], out int n))              count     = n;
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.WriteLine("[ERROR] --optimize-prompt requires --prompt argument");
        return 1;
    }

    libraryDb ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoWell", "photowell.db");

    using var optimizer = new PromptOptimizer(evalDb, libraryDb, model, prompt);
    var result = await optimizer.RunAsync(count);

    return result.IsAcceptable ? 0 : 1;  // Exit code based on quality
}

// ── Show detailed comparison results ──────────────────────────────────────────
if (args[0] == "--show-comparison")
{
    if (args.Length < 2)
    {
        Console.WriteLine("[ERROR] --show-comparison requires run ID");
        return 1;
    }

    string showRunId = args[1];
    string showEvalDb = "evaluations.db";

    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--eval-db" && i + 1 < args.Length)
            showEvalDb = args[++i];

    using var showDb = new EvalDatabase(showEvalDb);
    showDb.EnsurePromptOptSchema();

    var scores = showDb.GetPromptOptScores(showRunId);
    var sorted = scores.OrderBy(s => s.Similarity).ToList();

    Console.WriteLine($"\nWORST 3 MATCHES:");
    Console.WriteLine();
    foreach (var (file, claude, ollamaDesc, sim) in sorted.Take(3))
    {
        Console.WriteLine($"[{file}] Similarity: {sim:F3}");
        Console.WriteLine($"  Claude: {claude}");
        Console.WriteLine($"  Ollama: {ollamaDesc}");
        Console.WriteLine();
    }

    return 0;
}

// ── Accept prompt optimization run ────────────────────────────────────────────
if (args[0] == "--accept-prompt")
{
    if (args.Length < 2)
    {
        Console.WriteLine("[ERROR] --accept-prompt requires a run ID");
        return 1;
    }

    string acceptRunId = args[1];
    string acceptEvalDb = "evaluations.db";

    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--eval-db" && i + 1 < args.Length)
            acceptEvalDb = args[++i];

    using var acceptDb = new EvalDatabase(acceptEvalDb);
    acceptDb.EnsurePromptOptSchema();

    if (acceptDb.AcceptPromptOptRun(acceptRunId))
    {
        Console.WriteLine($"[OK] Accepted run: {acceptRunId}");
        Console.WriteLine("[TODO] Update AppSettings.VisionAnalysisPrompt in PhotoWell.Common");
        Console.WriteLine("       Then rebuild and test the app");
        return 0;
    }
    else
    {
        Console.WriteLine($"[ERROR] Run not found: {acceptRunId}");
        return 1;
    }
}

// ── Multi-model ensemble mode ────────────────────────────────────────────────────
if (args[0] == "--ensemble")
{
    int ensembleCount = 50;
    string? ensemblePrompt = null;
    string? ensembleModelsStr = null;
    string ensembleEvalDb = "evaluations.db";

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--prompt" && i + 1 < args.Length) ensemblePrompt = args[++i];
        else if (args[i] == "--models" && i + 1 < args.Length) ensembleModelsStr = args[++i];
        else if (args[i] == "--eval-db" && i + 1 < args.Length) ensembleEvalDb = args[++i];
        else if (int.TryParse(args[i], out int n)) ensembleCount = n;
    }

    if (string.IsNullOrWhiteSpace(ensemblePrompt))
    {
        Console.WriteLine("[ERROR] --ensemble requires --prompt argument");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(ensembleModelsStr))
    {
        Console.WriteLine("[ERROR] --ensemble requires --models argument (comma-separated)");
        return 1;
    }

    var ensembleModels = ensembleModelsStr.Split(',').Select(m => m.Trim()).ToArray();

    Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║ Multi-Model Ensemble Evaluation");
    Console.WriteLine($"║ Models: {string.Join(", ", ensembleModels)}");
    Console.WriteLine($"║ Count: {ensembleCount} images");
    Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

    using var ensemble = new MultiModelEnsemble(ensembleModels);
    using var ensembleDb = new EvalDatabase(ensembleEvalDb);
    ensembleDb.EnsurePromptOptSchema();

    var corpusData = ensembleDb.GetCorpusSample(ensembleCount);
    if (corpusData.Count == 0)
    {
        Console.WriteLine("[ERROR] No ground truth descriptions in corpus.");
        return 1;
    }

    var results = new List<(string FileName, string BestModel, string Description, double Similarity)>();

    for (int i = 0; i < corpusData.Count; i++)
    {
        var entry = corpusData[i];
        Console.WriteLine($"[{i + 1}/{corpusData.Count}] {entry.FileName}");

        var ensembleResult = await ensemble.DescribeAsync(entry.ImageUsed, ensemblePrompt);

        if (ensembleResult.BestDescription == null)
        {
            Console.WriteLine($"  ✗ No valid descriptions generated");
            continue;
        }

        double similarity = DescriptionComparator.ComputeSimilarity(entry.Description, ensembleResult.BestDescription);
        results.Add((entry.FileName, ensembleResult.BestModel ?? "unknown", ensembleResult.BestDescription, similarity));

        Console.WriteLine($"  ✓ Best: {ensembleResult.BestModel} (sim: {similarity:F3})");
        if (ensembleResult.AllDescriptions.Count > 1)
        {
            foreach (var kvp in ensembleResult.AllDescriptions.Where(kv => kv.Key != ensembleResult.BestModel))
                Console.WriteLine($"    Alt: {kvp.Key}");
        }
    }

    // Summary
    if (results.Count > 0)
    {
        var avgSim = results.Average(r => r.Similarity);
        var modelCounts = results.GroupBy(r => r.BestModel).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║ Ensemble Results:");
        Console.WriteLine($"║   Tested: {results.Count} images");
        Console.WriteLine($"║   Avg similarity: {avgSim:F3}");
        Console.WriteLine($"║   Best performer selections:");
        foreach (var kvp in modelCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"║     {kvp.Key}: {kvp.Value} images ({100.0 * kvp.Value / results.Count:F1}%)");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
    }

    return 0;
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
    string claudeReportDir = @"C:\Dev\PhotoWell\eval";

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
string reportDir = @"C:\Dev\PhotoWell\eval";
Directory.CreateDirectory(reportDir);
string safeRunId  = runId.Replace(":", "-");
string reportPath = Path.Combine(reportDir, $"eval-{safeRunId}.md");
string report = MarkdownReportGenerator.Generate(runId, db, models, testCases);
await File.WriteAllTextAsync(reportPath, report);
Console.WriteLine();
Console.WriteLine($"Report saved: {reportPath}");

return 0;

public sealed record GroundTruthRecord(
    string FileName,
    string FilePath,
    string ImagePath,
    string Description,
    int? PhotoYear = null
);
