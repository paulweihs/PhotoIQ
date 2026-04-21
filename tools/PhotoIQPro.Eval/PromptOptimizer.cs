using System.Diagnostics;

namespace PhotoIQPro.Eval;

/// <summary>
/// Orchestrates prompt optimization using Claude Code's vision as the oracle:
/// 1. Samples N images from library
/// 2. Ollama generates descriptions with candidate prompt
/// 3. Compares against Claude Code's ground truth (provided externally)
/// 4. Compares via Jaccard similarity (0-1, higher = better)
/// 5. Shows improvement/regression to guide prompt iteration
/// </summary>
public sealed class PromptOptimizer : IDisposable
{
    private readonly EvalDatabase _db;
    private readonly OllamaRunner _ollama;
    private readonly string _libraryDbPath;
    private readonly string _model;
    private readonly string _prompt;

    public PromptOptimizer(
        string evalDbPath,
        string libraryDbPath,
        string ollamaModel = "llama3.2-vision",
        string prompt = "")
    {
        _db = new EvalDatabase(evalDbPath);
        _db.EnsureSchema();
        _db.EnsureCorpusSchema();
        _db.EnsurePromptOptSchema();

        _ollama = new OllamaRunner();
        _libraryDbPath = libraryDbPath;
        _model = ollamaModel;
        _prompt = prompt;
    }

    /// <summary>
    /// Runs the test phase: generates Ollama descriptions and compares against Claude Code ground truth.
    /// Ground truth descriptions must already be in reference_corpus table.
    /// </summary>
    public async Task<OptimizerResult> RunAsync(int imageCount = 300, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");

        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║ Prompt Optimization Test");
        Console.WriteLine($"║ Oracle: reference_corpus (Claude Code)  |  Test: {_model}");
        Console.WriteLine($"║ Run ID: {runId}");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // ── Sample images ─────────────────────────────────────────────────────
        Console.WriteLine($"[1/2] Sampling {imageCount} images from PhotoIQ library...");
        var alreadyInCorpus = _db.GetCorpusFileNames();
        var photos = CorpusSampler.Sample(imageCount, _libraryDbPath, alreadyInCorpus);

        if (photos.Count == 0)
        {
            Console.WriteLine("[ERROR] No photos available in library.");
            return new OptimizerResult(runId, 0, 0.0, [], false);
        }

        Console.WriteLine($"      → {photos.Count} images sampled (stratified by decade)");
        Console.WriteLine();

        // ── Load ground truth from corpus ──────────────────────────────────────
        Console.WriteLine($"[2/2] Generating test descriptions (Ollama) and comparing...");
        var corpus = _db.GetCorpusSample(photos.Count);

        if (corpus.Count == 0)
        {
            Console.WriteLine("[ERROR] No ground truth descriptions in corpus.");
            Console.WriteLine("        First, analyze images with Claude Code and store in reference_corpus.");
            return new OptimizerResult(runId, 0, 0.0, [], false);
        }

        var similarities = new List<double>();
        var perImageScores = new List<(string FileName, double Similarity)>();

        bool modelAvailable = await _ollama.IsModelAvailableAsync(_model);
        if (!modelAvailable)
        {
            Console.WriteLine($"[ERROR] Model '{_model}' not available in Ollama.");
            return new OptimizerResult(runId, corpus.Count, 0.0, [], false);
        }

        for (int i = 0; i < corpus.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var entry = corpus[i];
            string ollamaDesc = await _ollama.GenerateAsync(_model, _prompt, entry.ImageUsed);

            if (string.IsNullOrWhiteSpace(ollamaDesc))
            {
                Console.WriteLine($"      [{entry.FileName}] Ollama returned empty");
                continue;
            }

            double similarity = DescriptionComparator.ComputeSimilarity(entry.Description, ollamaDesc);
            similarities.Add(similarity);
            perImageScores.Add((entry.FileName, similarity));

            // Store per-image score
            _db.InsertPromptOptScore(runId, entry.FileName, entry.Description, ollamaDesc, similarity);

            if ((i + 1) % 10 == 0)
                Console.WriteLine($"      {i + 1}/{corpus.Count} → avg sim: {similarities.Average():F3}");

            // Rate limit Ollama calls
            await Task.Delay(100, ct);
        }

        Console.WriteLine();
        stopwatch.Stop();

        // ── Summary ───────────────────────────────────────────────────────────
        double avgSimilarity = similarities.Count > 0 ? similarities.Average() : 0.0;
        double minSimilarity = similarities.Count > 0 ? similarities.Min() : 0.0;
        double maxSimilarity = similarities.Count > 0 ? similarities.Max() : 0.0;

        // Find worst and best matches
        var sorted = perImageScores.OrderBy(s => s.Similarity).ToList();
        var worstMatches = sorted.Take(3);
        var bestMatches = sorted.TakeLast(3);

        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║ Results:");
        Console.WriteLine($"║   Tested:         {similarities.Count} images");
        Console.WriteLine($"║   Avg similarity: {avgSimilarity:F3} (0.0-1.0, higher is better)");
        Console.WriteLine($"║   Min similarity: {minSimilarity:F3}");
        Console.WriteLine($"║   Max similarity: {maxSimilarity:F3}");
        Console.WriteLine($"║   Time:           {stopwatch.Elapsed.TotalMinutes:F1} min");
        Console.WriteLine("║");
        Console.WriteLine("║ Worst matches (improve prompt logic for these):");
        foreach (var (file, sim) in worstMatches)
            Console.WriteLine($"║   {file,-30} {sim:F3}");
        Console.WriteLine("║");
        Console.WriteLine("║ Best matches (prompt working well):");
        foreach (var (file, sim) in bestMatches)
            Console.WriteLine($"║   {file,-30} {sim:F3}");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"Run ID: {runId}");
        Console.WriteLine($"To accept this prompt: dotnet run -- --accept-prompt {runId}");
        Console.WriteLine($"To try a new prompt: dotnet run -- --optimize-prompt 300 --prompt \"new prompt\"");
        Console.WriteLine();

        // Store run result (not yet accepted)
        _db.InsertPromptOptRun(runId, _prompt, _model, similarities.Count,
            avgSimilarity, minSimilarity, maxSimilarity, accepted: false);

        return new OptimizerResult(runId, similarities.Count, avgSimilarity, similarities, avgSimilarity >= 0.6);
    }

    public void Dispose()
    {
        _db?.Dispose();
        _ollama?.Dispose();
    }
}

public sealed record OptimizerResult(
    string RunId,
    int ImageCount,
    double AverageSimilarity,
    List<double> Similarities,
    bool IsAcceptable
);
