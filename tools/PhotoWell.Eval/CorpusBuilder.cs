using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PhotoWell.Eval;

/// <summary>
/// Rebuilds the reference corpus by:
/// 1. Sampling photos from the PhotoWell library that have Claude descriptions
/// 2. Using existing Claude Code vision descriptions (no API calls)
/// 3. Inserting into reference_corpus table with valid file paths
/// </summary>
public class CorpusBuilder
{
    private readonly string _evalDbPath;
    private readonly string _mainDbPath;

    public CorpusBuilder(string evalDbPath, string mainDbPath)
    {
        _evalDbPath = evalDbPath;
        _mainDbPath = mainDbPath;
    }

    /// <summary>
    /// Run the corpus building workflow using existing Claude descriptions.
    /// </summary>
    public async Task<CorpusBuildResult> RunAsync(int targetCount = 40, CancellationToken ct = default)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║ Build Ground Truth Corpus — Sample from Library");
        Console.WriteLine("║ Using existing Claude Code vision descriptions (no API)");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

        // Phase 1: Sample photos with existing descriptions
        var photos = Phase1_SamplePhotosWithDescriptions(targetCount);
        Console.WriteLine();

        if (photos.Count == 0)
        {
            Console.WriteLine("✗ No analyzed photos with descriptions available to sample");
            return new CorpusBuildResult(0, 0, 0, false);
        }

        // Phase 2: Insert into corpus
        var insertResult = await Phase2_InsertIntoCorpusAsync(photos, ct);
        Console.WriteLine();

        // Phase 3: Validate
        var validateResult = await Phase3_ValidateAsync(insertResult.InsertedCount, ct);
        Console.WriteLine();

        return new CorpusBuildResult(
            SampledCount: photos.Count,
            DescriptionsGenerated: photos.Count,
            InsertedCount: insertResult.InsertedCount,
            Success: insertResult.InsertedCount > 0 && validateResult.AllValid
        );
    }

    /// <summary>
    /// Phase 1: Sample photos from library that have Claude descriptions.
    /// Uses LibraryPhotoSampler to get photos with existing descriptions and readable images.
    /// </summary>
    private List<PhotoWithDescription> Phase1_SamplePhotosWithDescriptions(int targetCount)
    {
        Console.WriteLine("Phase 1: Sample Library Photos (with existing Claude descriptions)");
        Console.WriteLine("──────────────────────────────────────────────────────────────────\n");

        var libraryPhotos = LibraryPhotoSampler.Sample(targetCount, _mainDbPath);
        var photos = libraryPhotos
            .Where(p => File.Exists(p.FilePath) && !string.IsNullOrWhiteSpace(p.AiDescription))
            .Select(p => new PhotoWithDescription(p.FileName, p.FilePath, p.AiDescription))
            .ToList();

        Console.WriteLine($"Sampled {photos.Count} analyzed photos with descriptions (max {targetCount} requested)");
        Console.WriteLine($"All sampled photos have valid source files and Claude descriptions\n");

        return photos;
    }

    /// <summary>
    /// Phase 2: Insert descriptions into reference_corpus.
    /// </summary>
    private async Task<InsertResult> Phase2_InsertIntoCorpusAsync(
        List<PhotoWithDescription> photos,
        CancellationToken ct)
    {
        Console.WriteLine("Phase 2: Insert into Reference Corpus");
        Console.WriteLine("────────────────────────────────────\n");

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        var insertedCount = 0;

        foreach (var photo in photos)
        {
            cmd.CommandText = @"
                INSERT OR IGNORE INTO reference_corpus
                (file_name, file_path, image_used, claude_description, generated_by, created_at)
                VALUES ($name, $path, $image, $desc, 'claude-code', datetime('now'))";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", photo.FileName);
            cmd.Parameters.AddWithValue("$path", photo.FilePath);
            cmd.Parameters.AddWithValue("$image", photo.FilePath);  // Source file path (was already readable in LibraryPhotoSampler)
            cmd.Parameters.AddWithValue("$desc", photo.Description);

            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                insertedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {photo.FileName} — {ex.Message}");
            }
        }

        Console.WriteLine($"Inserted {insertedCount} entries into reference_corpus\n");
        return new InsertResult(insertedCount);
    }

    /// <summary>
    /// Phase 3: Validate final corpus.
    /// </summary>
    private async Task<ValidateResult> Phase3_ValidateAsync(int insertedCount, CancellationToken ct)
    {
        Console.WriteLine("Phase 3: Validate Corpus");
        Console.WriteLine("────────────────────────\n");

        using var conn = new SqliteConnection($"Data Source={_evalDbPath}");
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_corpus";
        var totalCount = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = @"
            SELECT file_name, file_path, image_used
            FROM reference_corpus
            ORDER BY id";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var validCount = 0;

        while (await reader.ReadAsync())
        {
            var filePath = reader.GetString(1);
            var imageUsed = reader.GetString(2);

            if (File.Exists(filePath) && File.Exists(imageUsed))
                validCount++;
        }

        Console.WriteLine($"Final corpus: {totalCount} entries");
        Console.WriteLine($"✓ Valid (both files exist): {validCount}");
        Console.WriteLine($"✗ Invalid: {totalCount - validCount}\n");

        return new ValidateResult(AllValid: validCount == totalCount);
    }
}

public record PhotoWithDescription(string FileName, string FilePath, string Description);
public record InsertResult(int InsertedCount);
public record ValidateResult(bool AllValid);
public record CorpusBuildResult(int SampledCount, int DescriptionsGenerated, int InsertedCount, bool Success);
