using Microsoft.Data.Sqlite;

namespace PhotoWell.Eval;

/// <summary>
/// Samples photos from the PhotoWell library for corpus building, stratified by decade.
/// Excludes any photos already present in the reference_corpus table.
/// </summary>
public static class CorpusSampler
{
    private static readonly HashSet<string> NativeFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    private static string DefaultDbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoWell", "photowell.db");

    /// <summary>
    /// Returns up to <paramref name="total"/> photos not yet in the corpus,
    /// stratified across decades so older photos are not crowded out by
    /// the larger modern collection.
    /// </summary>
    /// <param name="total">Target corpus size (e.g. 300).</param>
    /// <param name="libraryDbPath">Path to photowell.db; defaults to %LOCALAPPDATA%\PhotoWell\photowell.db.</param>
    /// <param name="alreadyInCorpus">Set of file_name values already processed — skipped.</param>
    public static List<CorpusCandidatePhoto> Sample(
        int                  total,
        string?              libraryDbPath,
        HashSet<string>      alreadyInCorpus)
    {
        libraryDbPath ??= DefaultDbPath;

        if (!File.Exists(libraryDbPath))
        {
            Console.WriteLine($"[ERROR] Library not found: {libraryDbPath}");
            return [];
        }

        // ── Pull candidates from library ──────────────────────────────────────
        var candidates = ReadCandidates(libraryDbPath, alreadyInCorpus, total * 8);

        if (candidates.Count == 0)
        {
            Console.WriteLine("[Sampler] No new photos found in library.");
            return [];
        }

        // ── Stratify by decade ────────────────────────────────────────────────
        // Group into decades; unknown year goes into a separate bucket.
        var byDecade = candidates
            .GroupBy(p => p.PhotoYear.HasValue ? (p.PhotoYear.Value / 10) * 10 : -1)
            .ToDictionary(g => g.Key, g => g.ToList());

        int decades = byDecade.Count(kv => kv.Key >= 0);
        // Target per decade: divide equally, but no bucket gets more than 2× the fair share.
        int fairShare    = decades > 0 ? total / Math.Max(decades, 1) : total;
        int perDecadeCap = Math.Max(fairShare, 20);  // at least 20 from any decade that has data

        var selected  = new List<CorpusCandidatePhoto>(total);
        var overflow  = new List<CorpusCandidatePhoto>(); // excess from large decades

        // Fill each decade up to the cap, collect overflow.
        foreach (var (decade, photos) in byDecade.OrderBy(kv => kv.Key))
        {
            if (decade < 0)
            {
                // Unknown year — add up to fairShare
                selected.AddRange(photos.Take(fairShare));
            }
            else
            {
                int take = Math.Min(photos.Count, perDecadeCap);
                selected.AddRange(photos.Take(take));
                if (photos.Count > take)
                    overflow.AddRange(photos.Skip(take));
            }
        }

        // Top up from overflow if we haven't hit total yet.
        if (selected.Count < total)
        {
            var rng  = new Random();
            var rest = overflow.OrderBy(_ => rng.Next()).Take(total - selected.Count);
            selected.AddRange(rest);
        }

        // Final shuffle so output order is random, not decade-sorted.
        selected = selected.OrderBy(_ => Guid.NewGuid()).ToList();

        Console.WriteLine($"[Sampler] {selected.Count} photos selected across " +
                          $"{byDecade.Count(kv => kv.Key >= 0)} decades.");
        return selected.Take(total).ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<CorpusCandidatePhoto> ReadCandidates(
        string          dbPath,
        HashSet<string> alreadyInCorpus,
        int             limit)
    {
        var results = new List<CorpusCandidatePhoto>(limit);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // DateTaken may be null for very old imports; still include them.
        cmd.CommandText =
            """
            SELECT FilePath, FileName, ThumbnailMedium, ThumbnailSmall,
                   CAST(strftime('%Y', DateTaken) AS INTEGER) AS year
            FROM MediaFiles
            ORDER BY RANDOM()
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string filePath  = reader.GetString(0);
            string fileName  = reader.GetString(1);
            string? thumbMed = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? thumbSm  = reader.IsDBNull(3) ? null : reader.GetString(3);
            int?   year      = reader.IsDBNull(4) ? null : reader.GetInt32(4);

            // Skip already-processed.
            if (alreadyInCorpus.Contains(fileName)) continue;

            // Resolve a readable image — prefer medium thumbnail (always JPEG).
            string? imagePath = null;
            if (thumbMed != null && File.Exists(thumbMed))       imagePath = thumbMed;
            else if (thumbSm != null && File.Exists(thumbSm))    imagePath = thumbSm;
            else if (File.Exists(filePath) &&
                     NativeFormats.Contains(Path.GetExtension(filePath)))
                imagePath = filePath;

            if (imagePath == null) continue;

            results.Add(new CorpusCandidatePhoto(fileName, filePath, imagePath, year));
        }

        return results;
    }
}

public sealed record CorpusCandidatePhoto(
    string  FileName,
    string  FilePath,
    string  ImagePath,   // thumbnail or original — what gets sent to Claude
    int?    PhotoYear
);
