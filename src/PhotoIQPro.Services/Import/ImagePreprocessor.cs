using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Import;

/// <summary>
/// Converts non-JPEG/PNG images to a temp JPEG so vision models can analyse them.
/// JPEG and PNG are returned as-is (no conversion, no temp file).
/// RAW formats not decodable by ImageSharp are attempted via WPF BitmapDecoder (WIC),
/// which picks up any Windows-installed Camera RAW / HEIC codecs.
/// Originals are NEVER modified.
/// </summary>
public sealed class ImagePreprocessor : IImagePreprocessor
{
    // Formats LLaVA / Ollama accepts natively — no conversion needed.
    private static readonly HashSet<string> NativeFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png" };

    // Formats ImageSharp can decode without extra plugins.
    private static readonly HashSet<string> ImageSharpFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public async Task<PreparedImage?> PrepareAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);

        // Already in a natively-supported format — return original, no temp file.
        if (NativeFormats.Contains(ext))
            return new PreparedImage(filePath, isTemp: false);

        var tempPath = Path.Combine(Path.GetTempPath(), $"photoiq_{Guid.NewGuid():N}.jpg");

        // Try ImageSharp first (fast, in-process, no WPF dependency).
        if (ImageSharpFormats.Contains(ext))
        {
            try
            {
                using var img = await Image.LoadAsync(filePath, ct);
                await img.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 90 }, ct);
                return new PreparedImage(tempPath, isTemp: true);
            }
            catch { /* fall through to WIC */ }
        }

        // Fall back to WPF BitmapDecoder (WIC) — handles HEIC (with codec), RAW formats
        // (with Windows Camera RAW codec), and anything else the OS knows about.
        try
        {
            var bytes = await ToJpegViaWicAsync(filePath, ct);
            if (bytes is null) return null;
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            return new PreparedImage(tempPath, isTemp: true);
        }
        catch { return null; }
    }

    private static Task<byte[]?> ToJpegViaWicAsync(string filePath, CancellationToken ct)
    {
        // WPF imaging must run on an STA thread; use a dedicated thread rather than
        // a thread-pool thread (which may be MTA).
        var tcs = new TaskCompletionSource<byte[]?>();

        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri(filePath, UriKind.Absolute),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];

                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                tcs.SetResult(ms.ToArray());
            }
            catch (OperationCanceledException) { tcs.SetCanceled(ct); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }
}
