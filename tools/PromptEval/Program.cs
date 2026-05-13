// PromptEval v2 — Oracle/Candidate/Judge loop for llama3.2-vision prompt tuning.
//
// Oracle   : Claude Code views each image directly → reference description.
// Candidate: Stored LLaMA description from DB (or fresh --rerun).
// Judge    : Claude Code compares oracle vs candidate → structured gap list.
// Score    : This tool aggregates gap rates per category across the batch.
// Gates    : Saved thresholds; --gate-check exits 1 on regression (CI-ready).
//
// WORKFLOW:
//   1. dotnet run -- --batch 30
//        → batches/batch_YYYYMMDD_HHmmss.json
//   2. Claude Code: read batch file, Read each image, annotate oracle_desc + gaps[],
//        save as batches/batch_YYYYMMDD_HHmmss_annotated.json
//   3. dotnet run -- --score batches/batch_..._annotated.json
//        → logs/run_YYYYMMDD_HHmmss.json  +  console summary
//   4. Claude Code: edit prompt in OllamaVisionService.cs (bump PromptVersion here too)
//   5. dotnet run -- --rerun batches/batch_YYYYMMDD_HHmmss.json
//        → batches/batch_..._rerun_TIMESTAMP.json  (fresh LLaMA descs, clears annotations)
//   6. Repeat from step 2 on the rerun batch
//   7. dotnet run -- --gate-set          → logs/gates.json (rates + 5pp headroom)
//   8. dotnet run -- --gate-check        → exit 0 pass / exit 1 fail
//
// LEGACY (still supported):
//   --count N          Re-describe N random photos with Ollama, print diff
//   --scrub-preview    Find bad DB descriptions
//   --scrub-apply      Null out bad descriptions + set VisionPending
//   --requeue          Re-queue scrubbed photos (NULL desc, Complete status → VisionPending)
//   --id GUID          Re-describe one specific photo

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

// ── Config ────────────────────────────────────────────────────────────────────
const string OllamaUrl = "http://127.0.0.1:11434";
const string Model     = "llama3.2-vision";

// Bump this whenever the prompt changes.  Embedded in batch + log files.
const string PromptVersion = "v12b";

var DbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "PhotoWell", "photowell.db");

// Production prompt — keep in sync with AppSettings.VisionAnalysisPrompt.
const string ProductionPrompt = """
    Describe this photo in 2-3 sentences for a searchable personal photo library.
    Start with a noun phrase naming the main subject — e.g. "A golden retriever", "Three children playing", "Two cyclists on a mountain trail". Never begin with "This", "The image", "The photo", or "The main subject". Then describe what is happening or the most specific visual details. If there is room, end with the setting or background.
    Before writing, count every person visible in the frame. Include the total in your opening noun phrase (e.g. "Five people", "Two children and one adult"). Do not omit any person who occupies more than a small corner of the frame. Always use gender-neutral terms — "person", "people", "child", "children", "adult", "toddler". Never use "man", "woman", "boy", "girl", or gendered pronouns ("he", "she", "his", "her").
    Never assume a silhouette, backlit shape, or partially visible figure is a person unless facial features or clear human body proportions are visible. If you cannot confirm it is a person, describe it as a shape, shadow, or by what it actually is (e.g. "a tree silhouette," "a glowing outline").
    Write in prose only — no bullet points, lists, ellipses (...), or paragraph breaks. Never repeat the same phrase or clause. Write in present tense. Ignore any date or time stamps visible in the image.
    Describe only what you can clearly see. Do not speculate about clothing patterns, accessories, or actions that are ambiguous, partially obscured, or blurry — omit uncertain details rather than guessing. If subjects are viewed from behind or their action is unclear, describe only what is evident. Do not specify exact colors of clothing, hair, or small objects unless you are certain — thumbnail images make colors ambiguous; use general terms ("dark jacket", "light dress") or omit the color.
    Do not attempt to read or identify text on product labels, food packaging, toy boxes, condiment jars, or any small print — describe the object by category only (e.g. "a jar of sauce", "a box of cereal"). Only quote text that is large and clearly legible, such as text on signs, banners, clothing logos, scoreboards, or large displays.
    """;

// ── Directory layout ──────────────────────────────────────────────────────────
var projectDir = FindProjectDir();
var batchDir   = Path.Combine(projectDir, "batches");
var logDir     = Path.Combine(projectDir, "logs");

// ── Arg parse ─────────────────────────────────────────────────────────────────
int     legacyCount  = 5;
string? specificId   = null;
string  prompt       = ProductionPrompt;
bool    scrubPreview = false;
bool    scrubApply   = false;
bool    batchMode    = false;
int     batchCount   = 30;
string? scoreFile    = null;
string? rerunFile    = null;
bool    gateSet      = false;
string? gateSetFrom  = null;
bool    gateCheck    = false;
bool    history      = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--batch":
            batchMode = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var bc)) { batchCount = bc; i++; }
            break;
        case "--score":
            if (i + 1 < args.Length) scoreFile = args[++i];
            break;
        case "--rerun":
            if (i + 1 < args.Length) rerunFile = args[++i];
            break;
        case "--gate-set":
            gateSet = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) gateSetFrom = args[++i];
            break;
        case "--gate-check":  gateCheck    = true; break;
        case "--history":     history      = true; break;
        case "--count":       if (i + 1 < args.Length) legacyCount = int.Parse(args[++i]); break;
        case "--id":          if (i + 1 < args.Length) specificId  = args[++i]; break;
        case "--prompt":      if (i + 1 < args.Length) prompt      = args[++i]; break;
        case "--scrub-preview": scrubPreview = true; break;
        case "--scrub-apply":   scrubApply   = true; break;
        case "--requeue":       return Requeue(DbPath);
    }
}

if (!File.Exists(DbPath)) { Error($"DB not found: {DbPath}"); return 1; }

// ── Route ─────────────────────────────────────────────────────────────────────
if (batchMode)          return GenerateBatch(DbPath, batchDir, batchCount);
if (scoreFile != null)  return ScoreBatch(scoreFile, logDir);
if (rerunFile != null)  return await RerunBatch(rerunFile, logDir);
if (gateSet)            return SetGates(gateSetFrom, logDir);
if (gateCheck)          return CheckGates(logDir);
if (history)            return ShowHistory(logDir);
if (scrubPreview || scrubApply) return RunScrub(DbPath, apply: scrubApply);

return await RunLegacyEval(DbPath, legacyCount, specificId, prompt);

// ═════════════════════════════════════════════════════════════════════════════
// COMMANDS
// ═════════════════════════════════════════════════════════════════════════════

// ── --batch ───────────────────────────────────────────────────────────────────

static int GenerateBatch(string dbPath, string batchDir, int count)
{
    Directory.CreateDirectory(batchDir);

    var rows = LoadRows(dbPath, null, count);
    if (rows.Count == 0) { Error("No photos with AiDescription found."); return 1; }

    var ts      = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var batchId = $"batch_{ts}";
    var outPath = Path.Combine(batchDir, $"{batchId}.json");

    var photos = rows.Select(r => new BatchPhoto(
        Id:            r.Id,
        Filename:      Path.GetFileName(r.FilePath),
        FilePath:      r.FilePath,
        FileExists:    File.Exists(r.FilePath),
        ThumbnailPath: r.ThumbnailSmall,
        LlamaDesc:     r.OldDesc ?? "",
        OracleDesc:    null,
        Gaps:          null)).ToArray();

    var batch = new BatchFile(batchId, PromptVersion, DateTime.UtcNow.ToString("O"), photos);
    File.WriteAllText(outPath, JsonSerializer.Serialize(batch, JsonOpts()));

    int missing = photos.Count(p => !p.FileExists);

    Header($"Batch generated — {photos.Length} photos  (prompt {PromptVersion})");
    Console.WriteLine($"  File     : {outPath}");
    if (missing > 0) Color($"  Missing  : {missing} files not on disk — skip those\n", ConsoleColor.Yellow);
    Console.WriteLine();
    Color("Gap categories for annotation:\n", ConsoleColor.Cyan);
    foreach (var g in Cfg.GapCategories)
        Console.WriteLine($"    {g}");
    Console.WriteLine();
    Color("Next steps:\n", ConsoleColor.Yellow);
    Console.WriteLine($"  1. Claude: Read {outPath}");
    Console.WriteLine("  2. For each photo: Read the image file (use thumbnail_path if file_path is a DNG),");
    Console.WriteLine("     write oracle_desc + gaps[]");
    Console.WriteLine($"  3. Save annotated file as: {Path.Combine(batchDir, batchId + "_annotated.json")}");
    Console.WriteLine($"  4. dotnet run -- --score {Path.Combine(batchDir, batchId + "_annotated.json")}");
    return 0;
}

// ── --score ───────────────────────────────────────────────────────────────────

static int ScoreBatch(string annotatedFile, string logDir)
{
    if (!File.Exists(annotatedFile)) { Error($"File not found: {annotatedFile}"); return 1; }

    BatchFile batch;
    try   { batch = JsonSerializer.Deserialize<BatchFile>(File.ReadAllText(annotatedFile), JsonOpts())!; }
    catch (Exception ex) { Error($"Cannot parse batch JSON: {ex.Message}"); return 1; }

    var annotated = batch.Photos.Where(p => p.Gaps != null).ToArray();
    if (annotated.Length == 0)
    {
        Error("No annotated photos (all gaps[] are null). Annotate the batch first.");
        return 1;
    }

    int total = annotated.Length;

    // Gap rate per category: photos containing that category / total annotated
    var rates = new Dictionary<string, double>();
    foreach (var cat in Cfg.GapCategories)
    {
        int n = annotated.Count(p => p.Gaps!.Contains(cat, StringComparer.OrdinalIgnoreCase));
        rates[cat] = Math.Round((double)n / total, 4);
    }
    double anyGapRate = Math.Round((double)annotated.Count(p => p.Gaps!.Length > 0) / total, 4);

    Directory.CreateDirectory(logDir);
    var ts      = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var runId   = $"run_{ts}";
    var logPath = Path.Combine(logDir, $"{runId}.json");

    var logPhotos = annotated.Select(p =>
        new RunLogPhoto(p.Id, p.Filename, p.LlamaDesc, p.OracleDesc, p.Gaps)).ToArray();
    var log = new RunLog(runId, batch.BatchId, batch.PromptVersion,
                         total, DateTime.UtcNow.ToString("O"), rates, logPhotos);

    File.WriteAllText(logPath, JsonSerializer.Serialize(log, JsonOpts()));

    // Console summary
    Header($"Eval run: {runId}  (prompt {batch.PromptVersion}, n={total})");
    Console.WriteLine();

    foreach (var (cat, rate) in rates.OrderByDescending(kv => kv.Value))
    {
        int  pct = (int)(rate * 100);
        var  bar = new string('█', pct / 4).PadRight(25);
        var  col = rate > 0.20 ? ConsoleColor.Red
                 : rate > 0.10 ? ConsoleColor.Yellow
                 : ConsoleColor.Green;
        Color($"  {cat,-22}  {bar}  {pct,3}%\n", col);
    }

    Console.WriteLine();
    int anyCount = annotated.Count(p => p.Gaps!.Length > 0);
    Console.WriteLine($"  Any gap  : {(int)(anyGapRate * 100)}%  ({anyCount}/{total} photos)");
    Console.WriteLine($"  Log      : {logPath}");
    Console.WriteLine();
    Color("Next: review, edit prompt, then:\n", ConsoleColor.Yellow);
    var originalBatch = annotatedFile.Replace("_annotated.json", ".json");
    Console.WriteLine($"  dotnet run -- --rerun {originalBatch}");
    return 0;
}

// ── --rerun ───────────────────────────────────────────────────────────────────

static async Task<int> RerunBatch(string batchFile, string logDir)
{
    // Accept either the original or annotated file
    var sourcePath = batchFile.EndsWith("_annotated.json")
        ? batchFile.Replace("_annotated.json", ".json")
        : batchFile;

    if (!File.Exists(sourcePath)) { Error($"File not found: {sourcePath}"); return 1; }

    BatchFile batch;
    try   { batch = JsonSerializer.Deserialize<BatchFile>(File.ReadAllText(sourcePath), JsonOpts())!; }
    catch (Exception ex) { Error($"Cannot parse batch JSON: {ex.Message}"); return 1; }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    if (!await IsOllamaAvailable(http, OllamaUrl)) { Error("Ollama not reachable."); return 1; }

    Header($"Re-running LLaMA on {batch.Photos.Length} photos  (new prompt: {PromptVersion})");
    Console.WriteLine();

    var updated = new List<BatchPhoto>();
    int ok = 0, failed = 0;

    for (int i = 0; i < batch.Photos.Length; i++)
    {
        var photo = batch.Photos[i];
        Console.Write($"  [{i + 1}/{batch.Photos.Length}] {photo.Filename,-35} ");

        if (!File.Exists(photo.FilePath))
        {
            Color("SKIP (file missing)\n", ConsoleColor.DarkGray);
            updated.Add(photo);
            continue;
        }

        var raw  = await DescribeAsync(http, OllamaUrl, Model, photo.FilePath, ProductionPrompt);
        var desc = raw is not null ? SimulateParseResponse(raw) : null;

        if (desc is null)
        {
            Color("empty/rejected\n", ConsoleColor.Red);
            // Keep old desc so we don't lose data; reset annotation
            updated.Add(photo with { OracleDesc = null, Gaps = null });
            failed++;
        }
        else
        {
            Color($"ok\n", ConsoleColor.Green);
            // Replace llama_desc with fresh output, clear annotations (need re-judging)
            updated.Add(photo with { LlamaDesc = desc, OracleDesc = null, Gaps = null });
            ok++;
        }
    }

    var ts         = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var newBatchId = $"{batch.BatchId}_rerun_{ts}";
    var dir        = Path.GetDirectoryName(sourcePath)!;
    var outPath    = Path.Combine(dir, $"{newBatchId}.json");

    var newBatch = batch with
    {
        BatchId       = newBatchId,
        PromptVersion = PromptVersion,
        CreatedAt     = DateTime.UtcNow.ToString("O"),
        Photos        = updated.ToArray()
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(newBatch, JsonOpts()));

    Console.WriteLine();
    Header("Rerun complete");
    Console.WriteLine($"  OK     : {ok}");
    Console.WriteLine($"  Failed : {failed}");
    Console.WriteLine($"  Saved  : {outPath}");
    Console.WriteLine();
    Color("Next: Claude annotates the new batch, then score it:\n", ConsoleColor.Yellow);
    Console.WriteLine($"  dotnet run -- --score {Path.Combine(dir, newBatchId + "_annotated.json")}");
    return 0;
}

// ── --gate-set ────────────────────────────────────────────────────────────────

static int SetGates(string? specificLog, string logDir)
{
    Directory.CreateDirectory(logDir);

    RunLog? log = null;
    if (specificLog is not null && File.Exists(specificLog))
    {
        log = JsonSerializer.Deserialize<RunLog>(File.ReadAllText(specificLog), JsonOpts());
    }
    else
    {
        var latest = Directory.EnumerateFiles(logDir, "run_*.json")
            .OrderByDescending(f => f).FirstOrDefault();
        if (latest is null) { Error("No run logs found. Run --score first."); return 1; }
        log = JsonSerializer.Deserialize<RunLog>(File.ReadAllText(latest), JsonOpts());
    }

    if (log is null) { Error("Could not parse run log."); return 1; }

    // Gate = current rate + 5pp headroom, clamped [5%, 50%]
    var gates = new Dictionary<string, double>();
    foreach (var cat in Cfg.GapCategories)
    {
        var rate = log.GapRates.TryGetValue(cat, out var r) ? r : 0.0;
        gates[cat] = Math.Round(Math.Clamp(rate + 0.05, 0.05, 0.50), 2);
    }

    var gatesObj  = new GatesFile(DateTime.UtcNow.ToString("O"), log.RunId, gates);
    var gatesPath = Path.Combine(logDir, "gates.json");
    File.WriteAllText(gatesPath, JsonSerializer.Serialize(gatesObj, JsonOpts()));

    Header($"Gates set from: {log.RunId}");
    Console.WriteLine();
    foreach (var (cat, gate) in gates)
        Console.WriteLine($"  {cat,-22}  ≤ {(int)(gate * 100),3}%");
    Console.WriteLine($"\n  Saved: {gatesPath}");
    return 0;
}

// ── --gate-check ──────────────────────────────────────────────────────────────

static int CheckGates(string logDir)
{
    var gatesPath = Path.Combine(logDir, "gates.json");
    if (!File.Exists(gatesPath)) { Error("No gates.json found. Run --gate-set first."); return 1; }

    var latest = Directory.EnumerateFiles(logDir, "run_*.json")
        .OrderByDescending(f => f).FirstOrDefault();
    if (latest is null) { Error("No run logs found. Run --score first."); return 1; }

    var gatesObj = JsonSerializer.Deserialize<GatesFile>(File.ReadAllText(gatesPath), JsonOpts())!;
    var log      = JsonSerializer.Deserialize<RunLog>(File.ReadAllText(latest), JsonOpts())!;

    Header($"Gate check — run: {log.RunId}");
    Console.WriteLine($"  Gates from: {gatesObj.FromRun}");
    Console.WriteLine();

    bool anyFailed = false;
    foreach (var cat in Cfg.GapCategories)
    {
        var gate = gatesObj.Gates.TryGetValue(cat, out var g) ? g : 0.50;
        var rate = log.GapRates.TryGetValue(cat, out var r)   ? r : 0.0;
        bool pass = rate <= gate;
        if (!pass) anyFailed = true;

        var sym = pass ? "✓" : "✗";
        var col = pass ? ConsoleColor.Green : ConsoleColor.Red;
        Color($"  {sym} {cat,-22}  {(int)(rate * 100),3}%  ≤  {(int)(gate * 100),3}%\n", col);
    }

    Console.WriteLine();
    if (anyFailed) { Color("GATE FAILED — one or more categories exceeded threshold.\n", ConsoleColor.Red); return 1; }
    Color("All gates passed.\n", ConsoleColor.Green);
    return 0;
}

// ── --history ─────────────────────────────────────────────────────────────────

static int ShowHistory(string logDir)
{
    if (!Directory.Exists(logDir)) { Color("No eval history yet.\n", ConsoleColor.DarkGray); return 0; }

    var logs = Directory.EnumerateFiles(logDir, "run_*.json")
        .OrderByDescending(f => f).ToList();

    if (logs.Count == 0) { Color("No eval history yet.\n", ConsoleColor.DarkGray); return 0; }

    Header($"Eval history — {logs.Count} run(s)");
    Console.WriteLine();
    Console.WriteLine($"  {"Run",-32}  {"Prompt",-7}  {"n",-5}  Top gap");
    Console.WriteLine($"  {new string('─', 32)}  {new string('─', 7)}  {new string('─', 5)}  {new string('─', 28)}");

    foreach (var logPath in logs)
    {
        try
        {
            var log    = JsonSerializer.Deserialize<RunLog>(File.ReadAllText(logPath), JsonOpts())!;
            var topGap = log.GapRates.OrderByDescending(kv => kv.Value).First();
            var col    = topGap.Value > 0.20 ? ConsoleColor.Red
                       : topGap.Value > 0.10 ? ConsoleColor.Yellow
                       : ConsoleColor.White;
            Console.Write($"  {log.RunId,-32}  {log.PromptVersion,-7}  {log.PhotoCount,-5}  ");
            Color($"{topGap.Key} {(int)(topGap.Value * 100)}%\n", col);
        }
        catch
        {
            Color($"  {Path.GetFileName(logPath)} (parse error)\n", ConsoleColor.DarkGray);
        }
    }

    Console.WriteLine();
    return 0;
}

// ═════════════════════════════════════════════════════════════════════════════
// LEGACY EVAL (--count / --id)
// ═════════════════════════════════════════════════════════════════════════════

static async Task<int> RunLegacyEval(string dbPath, int count, string? specificId, string prompt)
{
    var rows = LoadRows(dbPath, specificId, count);
    if (rows.Count == 0) { Error("No photos with AiDescription found."); return 1; }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    if (!await IsOllamaAvailable(http, OllamaUrl))
    {
        Error($"Ollama not reachable at {OllamaUrl}. Start with: ollama serve");
        return 1;
    }

    bool candidate = prompt != ProductionPrompt;
    Header($"PhotoWell Prompt Eval — {rows.Count} photo(s) — model: {Model}");
    if (candidate) Color("  CANDIDATE prompt active\n", ConsoleColor.Yellow);
    Console.WriteLine();

    int missing = 0, ok = 0;

    for (int i = 0; i < rows.Count; i++)
    {
        var (id, filePath, oldDesc, _) = rows[i];
        Header($"[{i + 1}/{rows.Count}] ID={id}  {Path.GetFileName(filePath)}");

        if (!File.Exists(filePath))
        {
            Color("  FILE MISSING\n\n", ConsoleColor.DarkGray);
            missing++;
            continue;
        }

        Color("  OLD:    ", ConsoleColor.DarkCyan);
        Console.WriteLine(oldDesc ?? "(null)");

        var raw = await DescribeAsync(http, OllamaUrl, Model, filePath, prompt);
        if (raw is null)
        {
            Color("  STORED: (no output — Ollama returned empty)\n", ConsoleColor.Red);
        }
        else
        {
            var stored = SimulateParseResponse(raw);
            Color("  STORED: ", ConsoleColor.Green);
            if (stored is null)
                Color("(rejected — loop or too short)\n", ConsoleColor.Red);
            else
            {
                Console.WriteLine(stored);
                ok++;
                var gaps = LegacyDetectGaps(oldDesc, stored);
                if (gaps.Count > 0)
                    Color("  GAPS:   " + string.Join(" | ", gaps) + "\n", ConsoleColor.Yellow);
            }
        }
        Console.WriteLine();
    }

    Header("Summary");
    Console.WriteLine($"  Evaluated : {rows.Count - missing}");
    Console.WriteLine($"  Got output: {ok}");
    Console.WriteLine($"  No output : {rows.Count - missing - ok}");
    if (missing > 0) Console.WriteLine($"  Missing   : {missing} (file not found)");
    return 0;
}

// ═════════════════════════════════════════════════════════════════════════════
// SCRUB + REQUEUE
// ═════════════════════════════════════════════════════════════════════════════

static int RunScrub(string dbPath, bool apply)
{
    Header(apply ? "Scrub — APPLY" : "Scrub — PREVIEW (read-only)");
    Console.WriteLine();

    var candidates = new List<(string Id, string FilePath, string Desc, string Reason)>();
    using var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();

    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = """
            SELECT Id, FilePath, AiDescription
            FROM   MediaFiles
            WHERE  AiDescription IS NOT NULL AND AiDescription != ''
            ORDER  BY length(AiDescription) DESC
            """;
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            string id   = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
            string path = rdr.GetString(1);
            string desc = rdr.GetString(2);
            var reason  = ClassifyBadDescription(desc);
            if (reason != null) candidates.Add((id, path, desc, reason));
        }
    }

    if (candidates.Count == 0) { Color("No bad descriptions found.\n", ConsoleColor.Green); return 0; }

    Color($"Found {candidates.Count} bad description(s):\n\n", ConsoleColor.Yellow);
    foreach (var (id, fp, desc, reason) in candidates)
    {
        Color($"  ID={id}  {Path.GetFileName(fp)}\n", ConsoleColor.Cyan);
        Color($"  Reason : {reason}\n", ConsoleColor.Yellow);
        Color($"  Preview: {(desc.Length > 120 ? desc[..120] + "…" : desc)}\n\n", ConsoleColor.DarkGray);
    }

    if (!apply)
    {
        Color($"Run with --scrub-apply to null out {candidates.Count} description(s).\n", ConsoleColor.Yellow);
        return 0;
    }

    int updated = 0;
    using (var tx = con.BeginTransaction())
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE MediaFiles SET AiDescription = NULL, AiModelUsed = NULL, AnalysisStatus = 4 WHERE Id = @id";
        cmd.Parameters.Add("@id", SqliteType.Text);
        foreach (var (id, _, _, _) in candidates) { cmd.Parameters["@id"].Value = id; cmd.ExecuteNonQuery(); updated++; }
        tx.Commit();
    }

    Color($"Nulled {updated} description(s) — set to VisionPending.\n", ConsoleColor.Green);
    return 0;
}

static int Requeue(string dbPath)
{
    Header("Requeue — NULL desc + Complete status → VisionPending");
    Console.WriteLine();

    using var con = new SqliteConnection($"Data Source={dbPath}");
    con.Open();

    int count;
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = """
            SELECT count(*) FROM MediaFiles
            WHERE (AiDescription IS NULL OR AiDescription = '')
              AND AiModelUsed IS NULL AND AnalysisStatus = 2
            """;
        count = Convert.ToInt32(cmd.ExecuteScalar());
    }

    if (count == 0) { Color("Nothing to requeue.\n", ConsoleColor.Green); return 0; }

    Color($"Found {count} photo(s) eligible.\n\n", ConsoleColor.Yellow);
    using (var tx = con.BeginTransaction())
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE MediaFiles SET AnalysisStatus = 4
            WHERE (AiDescription IS NULL OR AiDescription = '')
              AND AiModelUsed IS NULL AND AnalysisStatus = 2
            """;
        int n = cmd.ExecuteNonQuery();
        tx.Commit();
        Color($"Set {n} photo(s) to VisionPending.\n", ConsoleColor.Green);
    }
    return 0;
}

// ═════════════════════════════════════════════════════════════════════════════
// UTILITIES
// ═════════════════════════════════════════════════════════════════════════════

static string? ClassifyBadDescription(string desc)
{
    if (IsRepetitionLoop(desc)) return "repetition loop";
    var trimmed = TrimToCompleteSentences(desc, 3);
    if (IsRepetitionLoop(trimmed)) return "loop in first 3 sentences";
    if (desc.Length > 500) return $"over-length ({desc.Length} chars)";
    if (desc.Contains("... ...") || desc.Contains("… …")) return "ellipsis degeneration";
    // Truncated first sentence: model stopped mid-thought and left "..."
    if (System.Text.RegularExpressions.Regex.IsMatch(desc, @"\.\.\.\s*\n"))
        return "truncated first sentence (... pattern)";
    return null;
}

static string StripFillerOpener(string text)
{
    var m = System.Text.RegularExpressions.Regex.Match(text,
        @"^(?:The|This)\s+(?:image|photo|picture)\s+(?:shows?|depicts?|features?|captures?|presents?|displays?|illustrates?|portrays?)\s+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success)
        m = System.Text.RegularExpressions.Regex.Match(text,
            @"^The\s+main\s+subject(?:\s+of\s+(?:this|the)\s+(?:image|photo|picture))?\s+is\s+(?:an?\s+)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success) return text;
    var remainder = text[m.Length..].TrimStart();
    if (remainder.Length == 0) return text;
    return char.ToUpperInvariant(remainder[0]) + remainder[1..];
}

static string? SimulateParseResponse(string response)
{
    if (string.IsNullOrWhiteSpace(response)) return null;
    // Collapse non-ASCII whitespace (U+00A0, U+FFFD, etc.) so the repetition detector
    // sees clean word boundaries instead of garbage-token-broken n-grams.
    response = System.Text.RegularExpressions.Regex.Replace(response, @"[^\x00-\x7F\r\n]", " ");
    response = System.Text.RegularExpressions.Regex.Replace(response, @" {2,}", " ").Trim();
    if (string.IsNullOrWhiteSpace(response)) return null;
    response = StripFillerOpener(response);
    if (string.IsNullOrWhiteSpace(response)) return null;
    if (IsRepetitionLoop(response)) return null;
    // Ellipsis-paragraph fix: "A girl in a red t-shirt...\nShe is sitting" → "A girl in a red t-shirt. She is sitting"
    var normalized = System.Text.RegularExpressions.Regex.Replace(response.Trim(), @"\.{2,}\s*[\r\n]+\s*", ". ");
    normalized = System.Text.RegularExpressions.Regex.Replace(
        normalized,
        @"([^\.\!\?])\s*[\r\n]+\s*",
        "$1. ");
    var trimmed = TrimToCompleteSentences(normalized, 3);
    if (string.IsNullOrWhiteSpace(trimmed)) return null;
    if (IsRepetitionLoop(trimmed)) return null;
    var wc = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    if (wc < 15) return null;
    return NeutraliseGender(trimmed);
}

static string NeutraliseGender(string text)
{
    static string Subst(string s, string pattern, string replacement) =>
        System.Text.RegularExpressions.Regex.Replace(s, pattern, m =>
            char.IsUpper(m.Value[0])
                ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
                : replacement,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    text = Subst(text, @"\bwomen\b", "people");
    text = Subst(text, @"\bwoman\b", "person");
    text = Subst(text, @"\bmen\b",   "people");
    text = Subst(text, @"\bman\b",   "person");
    text = Subst(text, @"\bgirls\b", "children");
    text = Subst(text, @"\bgirl\b",  "child");
    text = Subst(text, @"\bboys\b",  "children");
    text = Subst(text, @"\bboy\b",   "child");

    text = Subst(text, @"\bshe\b", "they");
    text = Subst(text, @"\bhe\b",  "they");
    text = Subst(text, @"\bhis\b", "their");
    text = Subst(text, @"\bher\b", "their");
    text = Subst(text, @"\bhim\b", "them");

    text = System.Text.RegularExpressions.Regex.Replace(text, @"\bthey is\b",  "they are",  System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    text = System.Text.RegularExpressions.Regex.Replace(text, @"\bthey was\b", "they were", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    text = System.Text.RegularExpressions.Regex.Replace(text, @"\bthey has\b", "they have", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    return text;
}

static bool IsRepetitionLoop(string text)
{
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (words.Length < 10) return false;
    var dominant = words.GroupBy(w => w.ToLowerInvariant()).Max(g => g.Count());
    return dominant > words.Length * 0.4;
}

static string TrimToCompleteSentences(string text, int maxSentences)
{
    var sentences  = new List<string>();
    int searchFrom = 0;
    while (sentences.Count < maxSentences && searchFrom < text.Length)
    {
        int endIdx = -1;
        for (int i = searchFrom; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '.' or '!' or '?')
            {
                bool notEllipsis  = c != '.' || i == 0 || text[i - 1] != '.';
                bool atEnd        = i + 1 == text.Length;
                bool followedByWs = i + 1 < text.Length && text[i + 1] is ' ' or '\n' or '\r';
                if (notEllipsis && (atEnd || followedByWs)) { endIdx = i; break; }
            }
        }
        if (endIdx < 0) break;
        var sentence = text[searchFrom..(endIdx + 1)].Trim();
        if (sentence.Length > 15) sentences.Add(sentence);
        searchFrom = endIdx + 1;
    }
    return sentences.Count > 0 ? string.Join(" ", sentences) : text;
}

static List<(string Id, string FilePath, string? OldDesc, string? ThumbnailSmall)> LoadRows(
    string dbPath, string? specificId, int count)
{
    var result = new List<(string, string, string?, string?)>();
    using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    con.Open();

    string sql = specificId is not null
        ? "SELECT Id, FilePath, AiDescription, ThumbnailSmall FROM MediaFiles WHERE Id = @id"
        : $"""
          SELECT Id, FilePath, AiDescription, ThumbnailSmall FROM MediaFiles
          WHERE AiDescription IS NOT NULL AND AiDescription != ''
          ORDER BY RANDOM() LIMIT {count}
          """;

    using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    if (specificId is not null) cmd.Parameters.AddWithValue("@id", specificId);

    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
        result.Add((rdr.IsDBNull(0) ? "" : rdr.GetString(0), rdr.GetString(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3)));
    return result;
}

static async Task<string?> DescribeAsync(
    HttpClient http, string baseUrl, string model, string filePath, string prompt)
{
    byte[] bytes;
    try { bytes = await File.ReadAllBytesAsync(filePath); } catch { return null; }

    var body = JsonSerializer.Serialize(new
    {
        model, prompt,
        images  = new[] { Convert.ToBase64String(bytes) },
        stream  = false,
        options = new { num_predict = 450, temperature = 0f, seed = 42 }
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    try
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp    = await http.PostAsync($"{baseUrl}/api/generate", content);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var raw = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
    catch { return null; }
}

static async Task<bool> IsOllamaAvailable(HttpClient http, string baseUrl)
{
    try { return (await http.GetAsync($"{baseUrl}/api/tags")).IsSuccessStatusCode; }
    catch { return false; }
}

// Legacy heuristic gap detector (used by --count mode only)
static List<string> LegacyDetectGaps(string? old, string fresh)
{
    var gaps = new List<string>();
    var oldW = WordSet(old); var freshW = WordSet(fresh);
    var oldWc = old?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
    var freshWc = fresh.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    if (oldWc > 0 && freshWc < oldWc * 0.5) gaps.Add("new significantly shorter");
    bool oldP = HasWord(oldW, "person","man","woman","child","girl","boy","people","baby");
    bool newP = HasWord(freshW, "person","man","woman","child","girl","boy","people","baby");
    if (oldP && !newP) gaps.Add("people dropped from new");
    if (!oldP && newP) gaps.Add("people added (possible hallucination)");
    bool oldL = HasWord(oldW, "indoor","outdoor","park","beach","street","kitchen","restaurant","forest","mountain");
    bool newL = HasWord(freshW, "indoor","outdoor","park","beach","street","kitchen","restaurant","forest","mountain");
    if (oldL && !newL) gaps.Add("location context dropped");
    return gaps;
}

static HashSet<string> WordSet(string? s) =>
    s is null ? [] : s.ToLowerInvariant()
        .Split([' ',',','.','!','?','\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

static bool HasWord(HashSet<string> words, params string[] targets) => words.Overlaps(targets);

static string FindProjectDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (dir.GetFiles("PromptEval.csproj").Length > 0) return dir.FullName;
        dir = dir.Parent;
    }
    // Fallback: tools/PromptEval under CWD
    var fallback = Path.Combine(Directory.GetCurrentDirectory(), "tools", "PromptEval");
    return Directory.Exists(fallback) ? fallback : Directory.GetCurrentDirectory();
}

static JsonSerializerOptions JsonOpts() => new()
{
    WriteIndented          = true,
    PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// ── Console helpers ───────────────────────────────────────────────────────────
static void Header(string text) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(text); Console.ResetColor(); }
static void Color(string text, ConsoleColor c) { Console.ForegroundColor = c; Console.Write(text); Console.ResetColor(); }
static void Error(string text) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine("ERROR: " + text); Console.ResetColor(); }

// ═════════════════════════════════════════════════════════════════════════════
// JSON MODELS
// ═════════════════════════════════════════════════════════════════════════════

record BatchPhoto(
    string   Id,
    string   Filename,
    string   FilePath,
    bool     FileExists,
    string?  ThumbnailPath,
    string   LlamaDesc,
    string?  OracleDesc,
    string[]? Gaps);

record BatchFile(
    string       BatchId,
    string       PromptVersion,
    string       CreatedAt,
    BatchPhoto[] Photos);

record RunLogPhoto(
    string    Id,
    string    Filename,
    string    LlamaDesc,
    string?   OracleDesc,
    string[]? Gaps);

record RunLog(
    string                     RunId,
    string                     BatchId,
    string                     PromptVersion,
    int                        PhotoCount,
    string                     ScoredAt,
    Dictionary<string, double> GapRates,
    RunLogPhoto[]              Photos);

record GatesFile(
    string                     SetAt,
    string                     FromRun,
    Dictionary<string, double> Gates);

// ═════════════════════════════════════════════════════════════════════════════
// SHARED CONFIG (accessible from static local functions)
// ═════════════════════════════════════════════════════════════════════════════

static class Cfg
{
    /// <summary>
    /// Canonical gap categories. Claude uses these exact strings when annotating batches.
    /// --gate-set and --gate-check key off this list.
    /// </summary>
    public static readonly string[] GapCategories =
    [
        "missing_landmark",    // visible named location / landmark / sign text not identified
        "wrong_count",         // wrong number of people or primary objects
        "hallucination",       // confidently describes something not present in the photo
        "activity_inversion",  // wrong activity (e.g. sitting vs standing, walking vs running)
        "missing_subject",     // main subject omitted or replaced with something different
        "truncated",           // incomplete sentence or ... pattern left in description
        "filler_opener",       // starts with banned phrase (The main subject, This photo, etc.)
        "formatting",          // bullet points, numbered lists, title lines, or headers
        "too_short",           // fewer than 15 words
    ];
}
