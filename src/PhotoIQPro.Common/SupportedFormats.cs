namespace PhotoIQPro.Common;

/// <summary>
/// Canonical file-extension sets used across import, scanning, and UI file dialogs.
/// ThumbnailService maintains its own exhaustive RAW list for rendering purposes.
/// </summary>
public static class SupportedFormats
{
    public static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".heif", ".avif"
    };

    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".dng", ".raf", ".pef", ".srw", ".raw"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".m4v"
    };

    /// <summary>Formats that CLIP/vision models and ImageSharp accept natively — no preprocessing needed.</summary>
    public static readonly HashSet<string> NativeFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    public static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    /// <summary>All supported import extensions (photo + RAW + video).</summary>
    public static readonly HashSet<string> AllExtensions =
        PhotoExtensions.Concat(RawExtensions).Concat(VideoExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>WPF file dialog filter spec for photo files (e.g. "*.jpg;*.jpeg;...").</summary>
    public static string PhotoFilterSpec => string.Join(";", PhotoExtensions.Select(e => $"*{e}"));

    /// <summary>WPF file dialog filter spec for RAW files.</summary>
    public static string RawFilterSpec => string.Join(";", RawExtensions.Select(e => $"*{e}"));
}
