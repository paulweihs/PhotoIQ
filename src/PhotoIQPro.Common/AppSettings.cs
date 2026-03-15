namespace PhotoIQPro.Common;

public static class AppSettings
{
    private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro");
    public static string DatabasePath => Path.Combine(AppDataPath, "photoiq.db");
    public static string ThumbnailsPath => Path.Combine(AppDataPath, "thumbnails");
    public static string ModelsPath => Path.Combine(AppDataPath, "models");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ModelsPath);
    }
}
