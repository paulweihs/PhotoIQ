using Microsoft.Data.Sqlite;

namespace PhotoIQPro.PromptHarness;

/// <summary>
/// Queries the PhotoIQ app's SQLite library (read-only) to sample photos
/// that have a readable image file on disk.
/// </summary>
public static class ImageSampler
{
    private static readonly HashSet<string> NativeFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    private static string DefaultPhotoIQDb =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoIQPro", "photoiq.db");

    public static List<SampleImage> Sample(int count, string? photoIQDbPath = null)
    {
        photoIQDbPath ??= DefaultPhotoIQDb;

        if (!File.Exists(photoIQDbPath))
        {
            Console.WriteLine($"[ERROR] PhotoIQ library not found: {photoIQDbPath}");
            Console.WriteLine("        Run PhotoIQ at least once to create the library.");
            return [];
        }

        var results = new List<SampleImage>(count);
        string now  = DateTime.UtcNow.ToString("o");

        // Open read-only so we never lock the live app's DB.
        using var conn = new SqliteConnection($"Data Source={photoIQDbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // Oversample 5× — many images may be offline or RAW-without-thumbnail.
        cmd.CommandText = """
            SELECT FilePath, FileName, ThumbnailMedium, ThumbnailSmall
            FROM MediaFiles
            WHERE AiDescription IS NOT NULL AND AiDescription != ''
            ORDER BY RANDOM()
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", count * 5);

        using var reader = cmd.ExecuteReader();
        while (reader.Read() && results.Count < count)
        {
            string filePath  = reader.GetString(0);
            string fileName  = reader.GetString(1);
            string? thumbMed = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? thumbSm  = reader.IsDBNull(3) ? null : reader.GetString(3);

            // Prefer thumbnail — always JPEG, works for RAW/DNG sources too.
            string? thumbPath = null;
            if (thumbMed != null && File.Exists(thumbMed))       thumbPath = thumbMed;
            else if (thumbSm != null && File.Exists(thumbSm))    thumbPath = thumbSm;
            else if (File.Exists(filePath) &&
                     NativeFormats.Contains(Path.GetExtension(filePath)))
                thumbPath = filePath;

            if (thumbPath == null) continue;

            results.Add(new SampleImage
            {
                ImagePath = filePath,
                ThumbPath = thumbPath,
                FileName  = fileName,
                SampledAt = now
            });
        }

        Console.WriteLine($"[Sampler] {results.Count}/{count} photos found with readable images.");
        return results;
    }
}
