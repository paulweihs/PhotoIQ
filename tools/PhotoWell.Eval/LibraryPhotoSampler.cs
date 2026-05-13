using Microsoft.Data.Sqlite;

namespace PhotoWell.Eval;

/// <summary>A photo from the app library that has an AI description and a readable image on disk.</summary>
public sealed record LibraryPhoto(
    string FileName,
    string FilePath,
    string AiDescription,
    string ImagePath       // path actually sent to Claude (thumbnail or original)
);

/// <summary>
/// Queries the PhotoWell app's SQLite library to sample photos that have AI descriptions
/// and for which a readable image file (thumbnail or JPEG/PNG original) exists on disk.
/// </summary>
public static class LibraryPhotoSampler
{
    private static readonly HashSet<string> NativeFormats =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    private static string DefaultDbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoWell", "photowell.db");

    public static List<LibraryPhoto> Sample(int count, string? dbPath = null, string? promptVersion = null)
    {
        dbPath ??= DefaultDbPath;

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"[ERROR] PhotoWell library not found: {dbPath}");
            Console.WriteLine("        Make sure PhotoWell has been run at least once and has photos.");
            return [];
        }

        var results = new List<LibraryPhoto>(count);

        // Open read-only so we never lock the live app's DB.
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // Oversample by 5× — many files may be offline, RAW without thumbnails, or missing.
        string promptFilter = promptVersion != null
            ? "AND PromptVersion = $promptVersion"
            : "";
        cmd.CommandText = $"""
            SELECT FilePath, FileName, AiDescription, ThumbnailMedium, ThumbnailSmall
            FROM MediaFiles
            WHERE AiDescription IS NOT NULL AND AiDescription != ''
            {promptFilter}
            ORDER BY RANDOM()
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", count * 5);
        if (promptVersion != null)
            cmd.Parameters.AddWithValue("$promptVersion", promptVersion);

        if (promptVersion != null)
            Console.WriteLine($"[Sampler] Filtering to prompt version: {promptVersion}");

        using var reader = cmd.ExecuteReader();
        while (reader.Read() && results.Count < count)
        {
            string filePath   = reader.GetString(0);
            string fileName   = reader.GetString(1);
            string aiDesc     = reader.GetString(2);
            string? thumbMed  = reader.IsDBNull(3) ? null : reader.GetString(3);
            string? thumbSm   = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Prefer thumbnail — always JPEG and works for RAW/DNG sources too.
            string? imagePath = null;
            if (thumbMed  != null && File.Exists(thumbMed))  imagePath = thumbMed;
            else if (thumbSm != null && File.Exists(thumbSm)) imagePath = thumbSm;
            else if (File.Exists(filePath) &&
                     NativeFormats.Contains(Path.GetExtension(filePath)))
                imagePath = filePath;

            if (imagePath == null) continue;

            results.Add(new LibraryPhoto(fileName, filePath, aiDesc, imagePath));
        }

        Console.WriteLine($"[Sampler] {results.Count}/{count} photos found with readable images.");
        return results;
    }
}
