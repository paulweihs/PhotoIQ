using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace PhotoIQPro.Eval;

/// <summary>
/// Rebuilds the evaluation corpus by:
/// 1. Identifying orphaned entries (source file missing)
/// 2. Deleting orphaned entries
/// 3. Validating remaining entries have both source and image files
/// 4. Optionally regenerating thumbnail paths
/// </summary>
public class CorpusRebuild
{
    private readonly string _evalDbPath;
    private readonly string _mainDbPath;

    public CorpusRebuild(string evalDbPath, string mainDbPath)
    {
        _evalDbPath = evalDbPath;
        _mainDbPath = mainDbPath;
    }

    /// <summary>
    /// Run the full corpus rebuild workflow.
    /// </summary>
    public async Task<CorpusRebuildResult> RunAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║ Test Corpus Rebuild — Thumbnail Validation & Cleanup");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

        using var evalDb = new EvalDatabase(_evalDbPath);
        evalDb.EnsureSchema();
        evalDb.EnsureCorpusSchema();

        // Phase 1: Inventory
        var inventoryResult = await Phase1_InventoryAsync(evalDb);
        Console.WriteLine();

        // Phase 2: Identify issues
        var issueResult = await Phase2_IdentifyIssuesAsync(evalDb);
        Console.WriteLine();

        // Phase 3: Cleanup orphaned entries
        var cleanupResult = await Phase3_CleanupAsync(evalDb, issueResult.OrphanedIds);
        Console.WriteLine();

        // Phase 4: Validate
        var validationResult = await Phase4_ValidateAsync(evalDb);
        Console.WriteLine();

        return new CorpusRebuildResult(
            TotalEntries: inventoryResult.Total,
            ValidEntries: inventoryResult.Valid,
            MissingSourceCount: issueResult.MissingSourceCount,
            MissingImageCount: issueResult.MissingImageCount,
            OrphanedCount: issueResult.MissingSourceCount,
            DeletedCount: cleanupResult.DeletedCount,
            FinalValidCount: validationResult.ValidCount,
            Success: validationResult.AllValid
        );
    }

    /// <summary>
    /// Phase 1: Count corpus entries and categorize state.
    /// </summary>
    private async Task<InventoryResult> Phase1_InventoryAsync(EvalDatabase db)
    {
        Console.WriteLine("Phase 1: Inventory Corpus");
        Console.WriteLine("─────────────────────────\n");

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_corpus";
        var total = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = @"
            SELECT id, file_name, file_path, image_used
            FROM reference_corpus
            ORDER BY id";

        using var reader = await cmd.ExecuteReaderAsync();
        var valid = 0;

        while (await reader.ReadAsync())
        {
            var filePath = reader.GetString(2);
            var imageUsed = reader.GetString(3);

            if (File.Exists(filePath) && File.Exists(imageUsed))
                valid++;
        }

        Console.WriteLine($"Total corpus entries: {total}");
        Console.WriteLine($"✓ Complete (both files exist): {valid}");
        Console.WriteLine($"✗ Incomplete (missing files): {total - valid}");

        return new InventoryResult(Total: (int)total, Valid: valid);
    }

    /// <summary>
    /// Phase 2: Categorize missing files.
    /// </summary>
    private async Task<IssueResult> Phase2_IdentifyIssuesAsync(EvalDatabase db)
    {
        Console.WriteLine("Phase 2: Identify Issues");
        Console.WriteLine("───────────────────────\n");

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, file_name, file_path, image_used
            FROM reference_corpus
            ORDER BY id";

        using var reader = await cmd.ExecuteReaderAsync();
        var missingSource = new List<int>();
        var missingImage = new List<int>();
        var missingSourceCount = 0;
        var missingImageCount = 0;

        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var fileName = reader.GetString(1);
            var filePath = reader.GetString(2);
            var imageUsed = reader.GetString(3);

            bool sourceExists = File.Exists(filePath);
            bool imageExists = File.Exists(imageUsed);

            if (!sourceExists && !imageExists)
            {
                missingSource.Add(id);
                missingSourceCount++;
                Console.WriteLine($"  Orphaned (both missing): ID {id} — {fileName}");
            }
            else if (!sourceExists)
            {
                missingSource.Add(id);
                missingSourceCount++;
                Console.WriteLine($"  Source missing: ID {id} — {fileName}");
            }
            else if (!imageExists)
            {
                missingImage.Add(id);
                missingImageCount++;
                Console.WriteLine($"  Image missing: ID {id} — {fileName} (source OK)");
            }
        }

        Console.WriteLine($"\nSummary:");
        Console.WriteLine($"  Source file missing: {missingSourceCount}");
        Console.WriteLine($"  Image file missing: {missingImageCount}");

        return new IssueResult(
            MissingSourceCount: missingSourceCount,
            MissingImageCount: missingImageCount,
            OrphanedIds: missingSource
        );
    }

    /// <summary>
    /// Phase 3: Delete orphaned entries.
    /// </summary>
    private async Task<CleanupResult> Phase3_CleanupAsync(EvalDatabase db, List<int> orphanedIds)
    {
        Console.WriteLine("Phase 3: Cleanup Orphaned Entries");
        Console.WriteLine("─────────────────────────────────\n");

        if (orphanedIds.Count == 0)
        {
            Console.WriteLine("No orphaned entries to delete.\n");
            return new CleanupResult(DeletedCount: 0);
        }

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        var idList = string.Join(",", orphanedIds);
        cmd.CommandText = $"DELETE FROM reference_corpus WHERE id IN ({idList})";
        var deletedCount = cmd.ExecuteNonQuery();

        Console.WriteLine($"Deleted {deletedCount} orphaned entries.\n");

        return new CleanupResult(DeletedCount: deletedCount);
    }

    /// <summary>
    /// Phase 4: Validate final corpus state.
    /// </summary>
    private async Task<ValidationResult> Phase4_ValidateAsync(EvalDatabase db)
    {
        Console.WriteLine("Phase 4: Validate Final Corpus");
        Console.WriteLine("──────────────────────────────\n");

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_corpus";
        var finalTotal = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = @"
            SELECT file_name, file_path, image_used
            FROM reference_corpus
            ORDER BY id";

        using var reader = await cmd.ExecuteReaderAsync();
        var validCount = 0;
        var invalidCount = 0;

        while (await reader.ReadAsync())
        {
            var fileName = reader.GetString(0);
            var filePath = reader.GetString(1);
            var imageUsed = reader.GetString(2);

            if (File.Exists(filePath) && File.Exists(imageUsed))
                validCount++;
            else
                invalidCount++;
        }

        bool allValid = invalidCount == 0;

        Console.WriteLine($"Final corpus size: {finalTotal} entries");
        Console.WriteLine($"✓ Valid entries: {validCount}");
        Console.WriteLine($"✗ Invalid entries: {invalidCount}");

        if (allValid)
            Console.WriteLine("\n✓ Corpus validation PASSED — all entries have source + image files");
        else
            Console.WriteLine($"\n⚠ Corpus validation WARNING — {invalidCount} entries still missing files");

        Console.WriteLine();

        return new ValidationResult(ValidCount: validCount, AllValid: allValid);
    }
}

public record InventoryResult(int Total, int Valid);
public record IssueResult(int MissingSourceCount, int MissingImageCount, List<int> OrphanedIds);
public record CleanupResult(int DeletedCount);
public record ValidationResult(int ValidCount, bool AllValid);
public record CorpusRebuildResult(
    int TotalEntries,
    int ValidEntries,
    int MissingSourceCount,
    int MissingImageCount,
    int OrphanedCount,
    int DeletedCount,
    int FinalValidCount,
    bool Success
);
