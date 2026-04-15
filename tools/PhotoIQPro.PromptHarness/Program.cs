// PhotoIQPro.PromptHarness
//
// Standalone prompt engineering + regression test harness for the llama3.2-vision prompt.
// Independent of the PhotoIQ app — reads PhotoIQ's SQLite library read-only for sampling.
//
// WORKFLOW:
//   1. --init [--count 100]          Sample images, save fixed test set to harness.db
//   2. --export-manifest             Output manifest.json → Claude generates gt.json
//   3. --import-gt gt.json           Store Claude's structured ground truth
//   4. --add-prompt v12 --file p.txt Register a prompt version
//   5. --quick-eval --prompt v12     Run Ollama, auto-score vs GT, show results
//   6. --rich-eval --prompt v12      Run Ollama, export review file → Claude annotates
//   7. --import-rich-scores r.json   Import Claude's rich scores
//   8. --history                     Compare all prompt versions
//   9. --promote v12                 Write winning prompt to AppSettings.cs
//
// REGRESSION:
//   --quick-eval auto-warns when a prompt scores below its parent.
//   Use --resample to replace the fixed image set (requires --force).

using System.Text;
using System.Text.Json;
using PhotoIQPro.PromptHarness;

// ── Config ────────────────────────────────────────────────────────────────────
string toolDir  = AppContext.BaseDirectory;
string dbPath   = Path.Combine(toolDir, "harness.db");
string gatesPath = Path.Combine(toolDir, "gates.json");

// ── Arg parse ─────────────────────────────────────────────────────────────────
if (args.Length == 0) { PrintUsage(); return 1; }

string command      = args[0];
int    count        = 100;
string model        = "llama3.2-vision";
string ollamaUrl    = "http://localhost:11434";
string? photoIQDb   = null;
string? promptVer   = null;
string? promptText  = null;
string? promptFile  = null;
string? parentVer   = null;
string? notes       = null;
string? outFile     = null;
string? inFile      = null;
string? runId       = null;
string? appSettings = null;
bool    force       = false;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--count"    when i + 1 < args.Length: int.TryParse(args[++i], out count); break;
        case "--model"    when i + 1 < args.Length: model       = args[++i]; break;
        case "--ollama"   when i + 1 < args.Length: ollamaUrl   = args[++i]; break;
        case "--db"       when i + 1 < args.Length: photoIQDb   = args[++i]; break;
        case "--prompt"   when i + 1 < args.Length: promptVer   = args[++i]; break;
        case "--text"     when i + 1 < args.Length: promptText  = args[++i]; break;
        case "--file"     when i + 1 < args.Length: promptFile  = args[++i]; break;
        case "--parent"   when i + 1 < args.Length: parentVer   = args[++i]; break;
        case "--notes"    when i + 1 < args.Length: notes       = args[++i]; break;
        case "--out"      when i + 1 < args.Length: outFile     = args[++i]; break;
        case "--run"      when i + 1 < args.Length: runId       = args[++i]; break;
        case "--app-settings" when i + 1 < args.Length: appSettings = args[++i]; break;
        case "--force": force = true; break;
        default:
            // Positional arg after command (e.g. version name for --add-prompt, file for --import-*)
            if (promptVer == null && command == "--add-prompt") promptVer = args[i];
            else if (inFile == null) inFile = args[i];
            break;
    }
}

// ── Load gates config ─────────────────────────────────────────────────────────
if (!File.Exists(gatesPath))
{
    Console.WriteLine($"[ERROR] gates.json not found at: {gatesPath}");
    return 1;
}
var jsonOpts   = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var gatesJson  = await File.ReadAllTextAsync(gatesPath);
var gatesConfig = JsonSerializer.Deserialize<GatesConfig>(gatesJson, jsonOpts)
    ?? throw new InvalidOperationException("Failed to parse gates.json");

// ── Open DB ───────────────────────────────────────────────────────────────────
using var db = new HarnessDb(dbPath);
db.EnsureSchema();

// ── Dispatch ──────────────────────────────────────────────────────────────────
return command switch
{
    "--init"               => await InitAsync(db, count, photoIQDb),
    "--resample"           => await ResampleAsync(db, count, photoIQDb, force),
    "--export-manifest"    => ExportManifest(db, outFile),
    "--import-gt"          => ImportGt(db, inFile),
    "--add-prompt"         => AddPrompt(db, promptVer, promptText, promptFile, parentVer, notes),
    "--quick-eval"         => await QuickEvalAsync(db, gatesConfig, promptVer, model, ollamaUrl, outFile),
    "--rich-eval"          => await RichEvalAsync(db, gatesConfig, promptVer, model, ollamaUrl, outFile),
    "--import-rich-scores" => ImportRichScores(db, inFile),
    "--history"            => History(db, gatesConfig),
    "--report"             => Report(db, gatesConfig, runId, outFile),
    "--show-results"       => ShowResults(db, gatesConfig, runId, count),
    "--promote"            => Promote(db, promptVer, appSettings),
    _ => UnknownCommand(command)
};

// ══════════════════════════════════════════════════════════════════════════════
// Commands
// ══════════════════════════════════════════════════════════════════════════════

async Task<int> InitAsync(HarnessDb db, int n, string? srcDb)
{
    int existing = db.GetSampleCount();
    if (existing > 0)
    {
        Console.WriteLine($"[WARN] Sample set already has {existing} images.");
        Console.WriteLine("       Use --resample --force to replace it.");
        return 1;
    }
    return await DoSampleAsync(db, n, srcDb);
}

async Task<int> ResampleAsync(HarnessDb db, int n, string? srcDb, bool f)
{
    int existing = db.GetSampleCount();
    if (existing > 0 && !f)
    {
        Console.WriteLine($"[WARN] This will replace {existing} existing images and their ground truth.");
        Console.WriteLine("       Re-run with --force to confirm.");
        return 1;
    }
    if (existing > 0)
    {
        db.ClearSample();
        Console.WriteLine($"[INFO] Cleared {existing} existing images.");
    }
    return await DoSampleAsync(db, n, srcDb);
}

async Task<int> DoSampleAsync(HarnessDb db, int n, string? srcDb)
{
    var images = await Task.Run(() => ImageSampler.Sample(n, srcDb));
    if (images.Count == 0) { Console.WriteLine("[ERROR] No images sampled."); return 1; }

    foreach (var img in images)
        db.InsertSampleImage(img);

    Console.WriteLine($"[OK] Sampled {images.Count} images into harness.db.");
    Console.WriteLine($"     Next: run --export-manifest, generate gt.json, then --import-gt.");
    return 0;
}

int ExportManifest(HarnessDb db, string? outPath)
{
    var images = db.GetSampleImages();
    if (images.Count == 0) { Console.WriteLine("[ERROR] No sample images. Run --init first."); return 1; }

    var manifest = new ManifestFile
    {
        GeneratedAt = DateTime.UtcNow.ToString("o"),
        Count       = images.Count,
        Images      = images.Select(i => new ManifestEntry
        {
            Id        = i.Id,
            ThumbPath = i.ThumbPath,
            FileName  = i.FileName
        }).ToArray()
    };

    outPath ??= Path.Combine(toolDir, "manifest.json");
    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, opts));
    Console.WriteLine($"[OK] Manifest written: {outPath}");
    Console.WriteLine($"     {images.Count} images. GT coverage: {db.GetGroundTruthCount()}/{images.Count}");
    Console.WriteLine();
    Console.WriteLine("  NEXT STEP — generate gt.json:");
    Console.WriteLine("  Read each image in the manifest using vision, then produce a JSON file with this structure:");
    Console.WriteLine("""
      {
        "generated_at": "<ISO timestamp>",
        "images": [
          {
            "id": <same id from manifest>,
            "file_name": "<file name>",
            "subject": "<primary subject phrase, lowercase, e.g. 'three children playing'>",
            "person_count": <integer, or -1 if no people>,
            "setting": "<setting phrase, e.g. 'indoor gym' or 'outdoor park'>",
            "key_objects": ["object1", "object2", ...],
            "verified_absent": ["thing not in image", ...],
            "free_text": "<your full description in 2-3 sentences>"
          }, ...
        ]
      }
    """);
    Console.WriteLine($"  Then: --import-gt <path-to-gt.json>");
    return 0;
}

int ImportGt(HarnessDb db, string? path)
{
    if (path == null) { Console.WriteLine("[ERROR] Provide path to gt.json."); return 1; }
    if (!File.Exists(path)) { Console.WriteLine($"[ERROR] File not found: {path}"); return 1; }

    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var file = JsonSerializer.Deserialize<GroundTruthFile>(File.ReadAllText(path), opts);
    if (file?.Images == null) { Console.WriteLine("[ERROR] Invalid gt.json format."); return 1; }

    int imported = 0, skipped = 0;
    foreach (var entry in file.Images)
    {
        var gt = new GroundTruth
        {
            ImageId        = entry.Id,
            Subject        = entry.Subject,
            PersonCount    = entry.PersonCount,
            Setting        = entry.Setting,
            KeyObjects     = entry.KeyObjects,
            VerifiedAbsent = entry.VerifiedAbsent,
            FreeText       = entry.FreeText,
            GeneratedAt    = file.GeneratedAt
        };
        try { db.UpsertGroundTruth(gt); imported++; }
        catch { skipped++; }
    }

    Console.WriteLine($"[OK] Ground truth imported: {imported} entries ({skipped} skipped).");
    Console.WriteLine($"     Coverage: {db.GetGroundTruthCount()}/{db.GetSampleCount()} images.");
    return 0;
}

int AddPrompt(HarnessDb db, string? ver, string? text, string? file, string? parent, string? n)
{
    if (ver == null) { Console.WriteLine("[ERROR] Provide a version name (e.g. v12)."); return 1; }
    if (text == null && file == null) { Console.WriteLine("[ERROR] Provide --text or --file."); return 1; }

    string promptText = text ?? File.ReadAllText(file!).Trim();

    if (db.GetPrompt(ver) != null)
    {
        Console.WriteLine($"[ERROR] Prompt version '{ver}' already exists. Use a different version name.");
        return 1;
    }

    db.InsertPrompt(new PromptRecord
    {
        Version       = ver,
        Text          = promptText,
        ParentVersion = parent,
        CreatedAt     = DateTime.UtcNow.ToString("o"),
        Notes         = n
    });

    Console.WriteLine($"[OK] Prompt '{ver}' registered.");
    if (parent != null) Console.WriteLine($"     Parent: {parent}");
    return 0;
}

async Task<int> QuickEvalAsync(
    HarnessDb db, GatesConfig gates, string? pver, string mdl, string ollamaBase, string? reportOut)
{
    var (prompt, images, gtMap) = LoadEvalPrereqs(db, pver, "quick");
    if (prompt == null || images == null || gtMap == null) return 1;

    using var ollama = new OllamaClient(ollamaBase);
    if (!await ollama.IsModelAvailableAsync(mdl))
    {
        Console.WriteLine($"[ERROR] Model '{mdl}' not available in Ollama at {ollamaBase}.");
        Console.WriteLine($"        Run: ollama pull {mdl}");
        return 1;
    }

    string rid     = $"{prompt.Version}-quick-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
    var    run     = new EvalRun
    {
        RunId         = rid,
        PromptVersion = prompt.Version,
        RunType       = "quick",
        OllamaModel   = mdl,
        StartedAt     = DateTime.UtcNow.ToString("o")
    };
    db.InsertRun(run);

    var scores  = new List<int>();
    var maxScores = new List<int>();
    int skipped = 0;

    Console.WriteLine($"┌─ Quick eval — {prompt.Version} — {images.Count} images");

    foreach (var img in images)
    {
        Console.Write($"│  [{img.FileName}] ");

        if (!File.Exists(img.ThumbPath))
        {
            Console.WriteLine("[SKIP — file offline]");
            skipped++;
            continue;
        }

        if (!gtMap.TryGetValue(img.Id, out var gt))
        {
            Console.WriteLine("[SKIP — no ground truth]");
            skipped++;
            continue;
        }

        string? desc = await ollama.GenerateAsync(mdl, prompt.Text, img.ThumbPath);
        if (string.IsNullOrWhiteSpace(desc))
        {
            Console.WriteLine("[SKIP — empty response]");
            skipped++;
            continue;
        }

        var gateScores = GateEvaluator.Score(desc, gt, gates);
        int total      = GateEvaluator.TotalScore(gateScores);
        int max        = GateEvaluator.MaxScore(gates, gt);
        bool passed    = total >= gates.PassThresholdPoints;

        scores.Add(total);
        maxScores.Add(max);
        if (passed) run.PassCount++; else run.FailCount++;

        string verdict = passed ? "PASS" : "FAIL";
        string gateStr = string.Join(" ", gateScores.Select(kv => $"{kv.Key}:{kv.Value}"));
        Console.WriteLine($"{total}/{max} [{verdict}] — {gateStr}");

        db.InsertResult(new EvalResult
        {
            RunId             = rid,
            ImageId           = img.Id,
            FileName          = img.FileName,
            OllamaDescription = desc,
            GateScores        = gateScores,
            TotalScore        = total,
            MaxScore          = max,
            Passed            = passed
        });

        await Task.Delay(300); // rate-limit Ollama
    }

    Console.WriteLine($"└──────────────────────────────────");

    run.ImagesEvaluated = scores.Count;
    run.ImagesSkipped   = skipped;
    run.AvgScore        = scores.Count > 0 ? scores.Average() : 0;
    run.AvgMaxScore     = maxScores.Count > 0 ? maxScores.Average() : 0;
    db.UpdateRunStats(run);

    PrintSummary(run, gates);
    CheckRegression(db, run, prompt);

    // Generate report
    string reportPath = reportOut ?? Path.Combine(toolDir, $"report-{rid}.md");
    var results       = db.GetResultsForRun(rid);
    var parentRecord  = prompt.ParentVersion != null ? db.GetPrompt(prompt.ParentVersion) : null;
    var parentRuns    = prompt.ParentVersion != null ? db.GetRunsForPrompt(prompt.ParentVersion) : null;
    string report     = ReportGenerator.Generate(run, results, gates, prompt, parentRecord, parentRuns);
    await File.WriteAllTextAsync(reportPath, report);
    Console.WriteLine($"\n  Report: {reportPath}");

    return run.PassCount > run.FailCount ? 0 : 1;
}

async Task<int> RichEvalAsync(
    HarnessDb db, GatesConfig gates, string? pver, string mdl, string ollamaBase, string? reviewOut)
{
    var (prompt, images, gtMap) = LoadEvalPrereqs(db, pver, "rich");
    if (prompt == null || images == null || gtMap == null) return 1;

    using var ollama = new OllamaClient(ollamaBase);
    if (!await ollama.IsModelAvailableAsync(mdl))
    {
        Console.WriteLine($"[ERROR] Model '{mdl}' not available in Ollama at {ollamaBase}.");
        return 1;
    }

    string rid = $"{prompt.Version}-rich-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
    var run = new EvalRun
    {
        RunId         = rid,
        PromptVersion = prompt.Version,
        RunType       = "rich",
        OllamaModel   = mdl,
        StartedAt     = DateTime.UtcNow.ToString("o")
    };
    db.InsertRun(run);

    var reviewEntries = new List<RichReviewEntry>();
    int skipped       = 0;

    Console.WriteLine($"┌─ Rich eval — {prompt.Version} — running Ollama on {images.Count} images");

    foreach (var img in images)
    {
        Console.Write($"│  [{img.FileName}] ");

        if (!File.Exists(img.ThumbPath))
        {
            Console.WriteLine("[SKIP — file offline]");
            skipped++;
            continue;
        }

        if (!gtMap.TryGetValue(img.Id, out var gt))
        {
            Console.WriteLine("[SKIP — no ground truth]");
            skipped++;
            continue;
        }

        string? desc = await ollama.GenerateAsync(mdl, prompt.Text, img.ThumbPath);
        if (string.IsNullOrWhiteSpace(desc))
        {
            Console.WriteLine("[SKIP — empty response]");
            skipped++;
            continue;
        }

        // Quick-score for reference (rich scores will replace these after annotation)
        var quickScores = GateEvaluator.Score(desc, gt, gates);
        int quickTotal  = GateEvaluator.TotalScore(quickScores);
        int quickMax    = GateEvaluator.MaxScore(gates, gt);
        bool quickPassed = quickTotal >= gates.PassThresholdPoints;

        Console.WriteLine($"quick={quickTotal}/{quickMax} [{(quickPassed ? "PASS" : "FAIL")}]");

        // Store with quick scores as placeholder; rich scores imported later
        db.InsertResult(new EvalResult
        {
            RunId             = rid,
            ImageId           = img.Id,
            FileName          = img.FileName,
            OllamaDescription = desc,
            GateScores        = quickScores,
            TotalScore        = quickTotal,
            MaxScore          = quickMax,
            Passed            = quickPassed,
            Notes             = "awaiting-rich-review"
        });

        reviewEntries.Add(new RichReviewEntry
        {
            Id                = img.Id,
            ThumbPath         = img.ThumbPath,
            FileName          = img.FileName,
            GtFreeText        = gt.FreeText,
            OllamaDescription = desc,
            QuickScore        = quickTotal,
            QuickMax          = quickMax,
            QuickPassed       = quickPassed
        });

        run.ImagesEvaluated++;
        await Task.Delay(300);
    }

    run.ImagesSkipped = skipped;
    run.AvgScore      = reviewEntries.Count > 0 ? reviewEntries.Average(e => e.QuickScore) : 0;
    run.AvgMaxScore   = reviewEntries.Count > 0 ? reviewEntries.Average(e => e.QuickMax) : 0;
    run.PassCount     = reviewEntries.Count(e => e.QuickPassed);
    run.FailCount     = reviewEntries.Count(e => !e.QuickPassed);
    db.UpdateRunStats(run);

    Console.WriteLine($"└──────────────────────────────────");
    Console.WriteLine($"  Ollama done. {reviewEntries.Count} descriptions collected.");

    // Write review file for Claude to annotate
    string reviewPath = reviewOut ?? Path.Combine(toolDir, $"rich-review-{rid}.json");
    var reviewFile = new RichReviewFile
    {
        RunId         = rid,
        PromptVersion = prompt.Version,
        GeneratedAt   = DateTime.UtcNow.ToString("o"),
        Images        = [.. reviewEntries]
    };
    var opts = new JsonSerializerOptions { WriteIndented = true };
    await File.WriteAllTextAsync(reviewPath, JsonSerializer.Serialize(reviewFile, opts));

    Console.WriteLine($"\n  Review file: {reviewPath}");
    Console.WriteLine($"  Run ID: {rid}");
    Console.WriteLine();
    Console.WriteLine("  NEXT STEP — rich annotation:");
    Console.WriteLine("  Read the review file. For each image, use vision to view the image,");
    Console.WriteLine("  compare to the ollama_description, and fill in rich_scores, rich_total,");
    Console.WriteLine("  rich_max, rich_passed, and rich_notes. Save as a new file, then:");
    Console.WriteLine($"  --import-rich-scores <annotated-file>");

    return 0;
}

int ImportRichScores(HarnessDb db, string? path)
{
    if (path == null) { Console.WriteLine("[ERROR] Provide path to rich scores JSON file."); return 1; }
    if (!File.Exists(path)) { Console.WriteLine($"[ERROR] File not found: {path}"); return 1; }

    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var file = JsonSerializer.Deserialize<RichReviewFile>(File.ReadAllText(path), opts);
    if (file?.Images == null) { Console.WriteLine("[ERROR] Invalid rich review file format."); return 1; }

    int updated = 0, skipped = 0;
    foreach (var entry in file.Images)
    {
        if (entry.RichScores == null || entry.RichTotal == null) { skipped++; continue; }
        db.UpdateRichScores(
            file.RunId, entry.Id,
            entry.RichScores,
            entry.RichTotal.Value,
            entry.RichMax ?? entry.QuickMax,
            entry.RichPassed ?? false,
            entry.RichNotes);
        updated++;
    }

    // Recompute run stats from rich scores
    var results = db.GetResultsForRun(file.RunId);
    // find and update the run aggregate
    Console.WriteLine($"[OK] Rich scores imported: {updated} updated, {skipped} without scores.");
    return 0;
}

int History(HarnessDb db, GatesConfig gates)
{
    var runs   = db.GetAllRuns();
    var prompts = db.GetAllPrompts().ToDictionary(p => p.Version);

    if (runs.Count == 0) { Console.WriteLine("No eval runs yet."); return 0; }

    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine("  PROMPT HISTORY");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  {"Version",-10} {"Type",-6} {"Avg",-8} {"Pass%",-8} {"Pass/Eval",-12} {"Date",-12} {"vs Parent"}");
    Console.WriteLine($"  {new string('-', 75)}");

    // Group by prompt version, show best run per version per type
    var byVersion = runs
        .GroupBy(r => (r.PromptVersion, r.RunType))
        .OrderBy(g => g.Key.RunType)
        .ThenBy(g => g.Min(r => r.StartedAt));

    foreach (var group in byVersion)
    {
        var best   = group.OrderByDescending(r => r.AvgScore).First();
        double pct = best.ImagesEvaluated > 0
            ? (double)best.PassCount / best.ImagesEvaluated * 100 : 0;

        string regression = "";
        if (prompts.TryGetValue(best.PromptVersion, out var p) && p.ParentVersion != null)
        {
            var parentBest = runs
                .Where(r => r.PromptVersion == p.ParentVersion && r.RunType == best.RunType
                         && r.ImagesEvaluated > 0)
                .OrderByDescending(r => r.AvgScore)
                .FirstOrDefault();
            if (parentBest != null)
            {
                double delta = best.AvgScore - parentBest.AvgScore;
                regression = delta >= 0 ? $"▲ +{delta:F1}" : $"▼ {delta:F1} *** REGRESSION";
            }
        }

        Console.WriteLine($"  {best.PromptVersion,-10} {best.RunType,-6} " +
                          $"{best.AvgScore:F1}/{best.AvgMaxScore:F1,-5} " +
                          $"{pct:F0}%     " +
                          $"{best.PassCount}/{best.ImagesEvaluated,-10} " +
                          $"{best.StartedAt[..10],-12} {regression}");
    }

    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine();
    return 0;
}

int Report(HarnessDb db, GatesConfig gates, string? rid, string? outPath)
{
    var runs = db.GetAllRuns();
    EvalRun? run;

    if (rid != null)
        run = runs.FirstOrDefault(r => r.RunId == rid);
    else
        run = runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();

    if (run == null) { Console.WriteLine("[ERROR] No runs found."); return 1; }

    var results      = db.GetResultsForRun(run.RunId);
    var prompt       = db.GetPrompt(run.PromptVersion);
    if (prompt == null) { Console.WriteLine($"[ERROR] Prompt '{run.PromptVersion}' not found."); return 1; }

    var parentRecord = prompt.ParentVersion != null ? db.GetPrompt(prompt.ParentVersion) : null;
    var parentRuns   = prompt.ParentVersion != null ? db.GetRunsForPrompt(prompt.ParentVersion) : null;

    string report    = ReportGenerator.Generate(run, results, gates, prompt, parentRecord, parentRuns);
    string path      = outPath ?? Path.Combine(toolDir, $"report-{run.RunId}.md");
    File.WriteAllText(path, report);
    Console.WriteLine($"[OK] Report: {path}");
    return 0;
}

int Promote(HarnessDb db, string? ver, string? appSettingsPath)
{
    if (ver == null) { Console.WriteLine("[ERROR] Provide a prompt version to promote."); return 1; }

    var prompt = db.GetPrompt(ver);
    if (prompt == null) { Console.WriteLine($"[ERROR] Prompt '{ver}' not found in harness."); return 1; }

    // Default: AppSettings.cs relative to the solution root
    appSettingsPath ??= FindAppSettings();
    if (appSettingsPath == null || !File.Exists(appSettingsPath))
    {
        Console.WriteLine("[ERROR] Could not find AppSettings.cs. Use --app-settings <path>.");
        return 1;
    }

    string src = File.ReadAllText(appSettingsPath);

    // Replace CurrentPromptVersion
    src = System.Text.RegularExpressions.Regex.Replace(
        src,
        @"public const string CurrentPromptVersion = ""[^""]+"";",
        $@"public const string CurrentPromptVersion = ""{ver}"";");

    // Replace VisionAnalysisPrompt (the raw string literal block)
    src = System.Text.RegularExpressions.Regex.Replace(
        src,
        @"public const string VisionAnalysisPrompt =\s*""""""\s*[\s\S]*?"""""";",
        $"public const string VisionAnalysisPrompt =\n        \"\"\"\n        {prompt.Text.Replace("\n", "\n        ")}\n        \"\"\";",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    File.WriteAllText(appSettingsPath, src);
    Console.WriteLine($"[OK] Promoted '{ver}' to AppSettings.cs.");
    Console.WriteLine($"     Path: {appSettingsPath}");
    Console.WriteLine($"     Rebuild the solution to apply.");
    return 0;
}

// ── Helpers ───────────────────────────────────────────────────────────────────

(PromptRecord? prompt, List<SampleImage>? images, Dictionary<int, GroundTruth>? gtMap)
    LoadEvalPrereqs(HarnessDb db, string? pver, string runType)
{
    PromptRecord? prompt;
    if (pver != null)
    {
        prompt = db.GetPrompt(pver);
        if (prompt == null)
        {
            Console.WriteLine($"[ERROR] Prompt '{pver}' not found. Use --add-prompt first.");
            return (null, null, null);
        }
    }
    else
    {
        // Use the most recently registered prompt
        var all = db.GetAllPrompts();
        prompt = all.LastOrDefault();
        if (prompt == null)
        {
            Console.WriteLine("[ERROR] No prompts registered. Use --add-prompt first.");
            return (null, null, null);
        }
        Console.WriteLine($"[INFO] Using latest prompt: {prompt.Version}");
    }

    var images = db.GetSampleImages();
    if (images.Count == 0)
    {
        Console.WriteLine("[ERROR] No sample images. Run --init first.");
        return (null, null, null);
    }

    var gtMap = db.GetAllGroundTruth();
    int missing = images.Count(i => !gtMap.ContainsKey(i.Id));
    if (gtMap.Count == 0)
    {
        Console.WriteLine("[ERROR] No ground truth. Run --export-manifest, generate gt.json, --import-gt.");
        return (null, null, null);
    }
    if (missing > 0)
        Console.WriteLine($"[WARN] {missing}/{images.Count} images have no ground truth — they will be skipped.");

    Console.WriteLine($"[INFO] Prompt: {prompt.Version}");
    Console.WriteLine($"[INFO] Sample: {images.Count} images, GT coverage: {gtMap.Count}/{images.Count}");
    Console.WriteLine();
    return (prompt, images, gtMap);
}

void PrintSummary(EvalRun run, GatesConfig gates)
{
    double pct = run.ImagesEvaluated > 0
        ? (double)run.PassCount / run.ImagesEvaluated * 100 : 0;

    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine($"  {run.RunType.ToUpper()} EVAL — {run.PromptVersion}");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine($"  Evaluated : {run.ImagesEvaluated}  Skipped: {run.ImagesSkipped}");
    Console.WriteLine($"  Pass      : {run.PassCount} ({pct:F0}%)");
    Console.WriteLine($"  Fail      : {run.FailCount}");
    Console.WriteLine($"  Avg score : {run.AvgScore:F1} / {run.AvgMaxScore:F1}");
    Console.WriteLine($"  Verdict   : {(pct >= 80 ? "PASS ✓" : "FAIL ✗")}");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
}

void CheckRegression(HarnessDb db, EvalRun run, PromptRecord prompt)
{
    if (prompt.ParentVersion == null) return;

    var parentRuns = db.GetRunsForPrompt(prompt.ParentVersion)
        .Where(r => r.RunType == run.RunType && r.ImagesEvaluated > 0)
        .OrderByDescending(r => r.AvgScore)
        .ToList();

    if (parentRuns.Count == 0) return;

    var best   = parentRuns.First();
    double delta = run.AvgScore - best.AvgScore;

    if (delta < 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  *** REGRESSION DETECTED ***");
        Console.WriteLine($"  {run.PromptVersion} avg {run.AvgScore:F1} < parent {prompt.ParentVersion} avg {best.AvgScore:F1} (Δ {delta:F1})");
        Console.WriteLine($"  Recommendation: discard {run.PromptVersion} and modify {prompt.ParentVersion} differently.");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine($"  Improvement vs {prompt.ParentVersion}: ▲ +{delta:F1} ({run.AvgScore:F1} vs {best.AvgScore:F1})");
    }
}

string? FindAppSettings()
{
    // Walk up from tool dir to find the solution root
    string? dir = toolDir;
    for (int i = 0; i < 6; i++)
    {
        dir = Path.GetDirectoryName(dir);
        if (dir == null) break;
        string candidate = Path.Combine(dir, "src", "PhotoIQPro.Common", "AppSettings.cs");
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

int ShowResults(HarnessDb db, GatesConfig gates, string? rid, int limit)
{
    var runs = db.GetAllRuns();
    EvalRun? run = rid != null
        ? runs.FirstOrDefault(r => r.RunId == rid)
        : runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();

    if (run == null) { Console.WriteLine("[ERROR] No runs found."); return 1; }

    var results = db.GetResultsForRun(run.RunId);
    var gtMap   = db.GetAllGroundTruth();

    Console.WriteLine($"Run: {run.RunId}  ({results.Count} results)");
    Console.WriteLine();

    int shown = 0;
    foreach (var r in results.OrderBy(r => r.TotalScore))
    {
        if (shown >= limit) break;
        shown++;

        string desc = r.OllamaDescription ?? "(null)";
        int words   = desc.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        gtMap.TryGetValue(r.ImageId, out var gt);

        Console.WriteLine($"[{r.FileName}] score={r.TotalScore}/{r.MaxScore}  words={words}");
        if (gt != null)
        {
            Console.WriteLine($"  GT setting    : {gt.Setting}");
            Console.WriteLine($"  GT person_cnt : {gt.PersonCount}");
            Console.WriteLine($"  GT subject    : {gt.Subject}");
        }
        Console.WriteLine($"  Gates         : {string.Join(" ", r.GateScores.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        Console.WriteLine($"  Description   : {desc}");
        Console.WriteLine();
    }
    return 0;
}

int UnknownCommand(string cmd)
{
    Console.WriteLine($"[ERROR] Unknown command: {cmd}");
    PrintUsage();
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("""
        PhotoIQPro.PromptHarness — Vision prompt engineering & regression testing

        SETUP:
          --init [--count N] [--db <photoiq.db>]
              Sample N images from PhotoIQ library (default: 100). Run once.

          --resample [--count N] [--db <path>] [--force]
              Replace sample set (requires --force to confirm).

          --export-manifest [--out manifest.json]
              Export image list. Claude reads images and generates gt.json.

          --import-gt <gt.json>
              Import Claude-generated structured ground truth.

          --add-prompt <version> (--text "<prompt>" | --file prompt.txt)
                                 [--parent <ver>] [--notes "<text>"]
              Register a prompt version for evaluation.

        EVALUATION:
          --quick-eval [--prompt <ver>] [--model llama3.2-vision] [--ollama <url>]
              Run Ollama, auto-score vs structured GT. Fast, no Claude needed.

          --rich-eval [--prompt <ver>] [--model llama3.2-vision] [--ollama <url>]
              Run Ollama, produce review file for Claude to annotate image-by-image.

          --import-rich-scores <annotated-review.json>
              Import Claude's rich scores from an annotated rich-eval file.

        ANALYSIS:
          --history
              Show all prompt versions and their scores side-by-side.

          --report [--run <run-id>] [--out report.md]
              Generate markdown report (defaults to most recent run).

          --promote <version> [--app-settings <path>]
              Write winning prompt to AppSettings.cs and bump CurrentPromptVersion.

        REGRESSION:
          quick-eval and rich-eval automatically compare to the parent prompt
          and warn when scores regress.
        """);
}
