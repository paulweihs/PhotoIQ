namespace PhotoIQPro.Common;

/// <summary>
/// Minimal append-only logger. Writes timestamped lines to vision.log in the app data folder.
/// Thread-safe via lock; never throws — logging must not crash the app.
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();

    public static void Vision(string message) => Write(AppSettings.VisionLogPath, "VISION", message);

    /// <summary>Logs application-level informational events to app.log.</summary>
    public static void Info(string message) => Write(AppSettings.AppLogPath, "INFO", message);

    /// <summary>Logs application-level errors (startup, DB migrations, background tasks) to app.log.</summary>
    public static void Error(string message) => Write(AppSettings.AppLogPath, "ERROR", message);

    private static void Write(string path, string level, string message)
    {
        try
        {
            // Timestamp captured inside the lock so concurrent callers always write
            // in the order they acquired it — prevents out-of-order log entries.
            lock (_lock)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }
}
