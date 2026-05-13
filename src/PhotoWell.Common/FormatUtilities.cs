namespace PhotoWell.Common;

/// <summary>Utility methods for formatting values for display in the UI.</summary>
public static class FormatUtilities
{
    /// <summary>Formats a byte count as a human-readable file size string (B, KB, MB, or GB).</summary>
    /// <remarks>Uses binary units (1024 bytes = 1 KB). Displays one decimal place for KB/MB/GB.</remarks>
    /// <param name="bytes">The file size in bytes.</param>
    /// <returns>A formatted string like "2.5 MB" or "1.2 GB".</returns>
    public static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB",
        _                => $"{bytes} B"
    };
}
