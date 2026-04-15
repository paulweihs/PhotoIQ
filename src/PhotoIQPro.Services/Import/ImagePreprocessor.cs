using Sdcb.LibRaw;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.Services.Import;

/// <summary>
/// Converts images to a temp JPEG suitable for vision model analysis.
/// JPEG/PNG within the size cap are returned as-is. Everything else is converted.
///
/// Conversion priority for RAW/DNG/CR2/NEF/ARW:
///   1. RawPreviewExtractor — pure .NET TIFF parser, extracts the embedded full-size
///      JPEG preview. Works on any machine, no codec installation required.
///   2. WPF BitmapDecoder (WIC) — fallback for files whose TIFF structure the
///      extractor can't parse (e.g. CR3 / ISOBMFF containers) when the Windows
///      Camera RAW codec is installed.
///   3. Returns null — preprocessor could not decode the file.
///
/// Originals are NEVER modified.
/// </summary>
public sealed class ImagePreprocessor : IImagePreprocessor
{
    // Vision models don't benefit from full-resolution images; cap at this size.
    private const int MaxVisionDimension = 1024;

    // Formats LLaVA / Ollama accepts natively — no conversion needed.
    private static readonly HashSet<string> NativeFormats = SupportedFormats.NativeFormats;

    // Formats ImageSharp can decode without extra plugins.
    private static readonly HashSet<string> ImageSharpFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public async Task<PreparedImage?> PrepareAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);

        // For natively-supported formats, still resize if the image is large.
        if (NativeFormats.Contains(ext))
        {
            try
            {
                var info = await Image.IdentifyAsync(filePath, ct);
                if (info != null && Math.Max(info.Width, info.Height) <= MaxVisionDimension)
                    return new PreparedImage(filePath, isTemp: false);

                // Needs resizing — fall through to ImageSharp path below.
            }
            catch (Exception ex)
            {
                AppLog.Vision($"[Preprocess] IdentifyAsync failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name} — returning original");
                return new PreparedImage(filePath, isTemp: false);
            }
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"photoiq_{Guid.NewGuid():N}.jpg");

        // Try ImageSharp first (fast, in-process, no WPF dependency).
        if (ImageSharpFormats.Contains(ext))
        {
            try
            {
                using var img = await Image.LoadAsync(filePath, ct);
                if (Math.Max(img.Width, img.Height) > MaxVisionDimension)
                    img.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(MaxVisionDimension, MaxVisionDimension),
                        Mode = ResizeMode.Max
                    }));
                await img.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 85 }, ct);
                return new PreparedImage(tempPath, isTemp: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Vision($"[Preprocess] ImageSharp failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name} — trying embedded preview");
            }
        }

        // Priority 1 — TIFF embedded JPEG preview (only if large enough to be useful).
        var previewBytes = RawPreviewExtractor.ExtractLargestJpegPreview(filePath);
        if (previewBytes != null)
        {
            try
            {
                using var ms      = new MemoryStream(previewBytes);
                using var preview  = await Image.LoadAsync(ms, ct);
                if (Math.Min(preview.Width, preview.Height) >= 500)
                {
                    if (Math.Max(preview.Width, preview.Height) > MaxVisionDimension)
                        preview.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Size = new Size(MaxVisionDimension, MaxVisionDimension),
                            Mode = ResizeMode.Max
                        }));
                    await preview.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 85 }, ct);
                    return new PreparedImage(tempPath, isTemp: true);
                }
                // Too small — fall through to LibRaw
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Vision($"[Preprocess] Embedded preview decode failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name} — trying LibRaw");
            }
        }

        // Priority 2 — LibRaw demosaics the RAW sensor data.
        // Works without any OS codec installation.
        try
        {
            var wrote = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var ctx = RawContext.OpenFile(filePath);
                ctx.Unpack();
                ctx.DcrawProcess(p =>
                {
                    p.UseCameraWb  = true;
                    p.OutputBps    = 8;
                });
                using var processed = ctx.MakeDcrawMemoryImage();
                // LibRaw outputs RGB24 — do NOT call SwapRGB() here.
                // SwapRGB() converts RGB→BGR for GDI+ Bitmaps; ImageSharp Rgb24 expects RGB.
                using var img = Image.LoadPixelData<Rgb24>(
                    processed.AsSpan<byte>(), processed.Width, processed.Height);
                if (Math.Max(img.Width, img.Height) > MaxVisionDimension)
                    img.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(MaxVisionDimension, MaxVisionDimension),
                        Mode = ResizeMode.Max
                    }));
                img.SaveAsJpeg(tempPath, new JpegEncoder { Quality = 85 });
                return true;
            }, ct);

            if (wrote)
                return new PreparedImage(tempPath, isTemp: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Vision($"[Preprocess] LibRaw failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name} — trying WIC");
        }

        // Priority 3 — WPF BitmapDecoder (WIC) — handles HEIC (with codec) and
        // container formats libraw doesn't support (e.g. CR3 / ISOBMFF).
        try
        {
            var bytes = await ToJpegViaWicAsync(filePath, ct);
            if (bytes is null) return null;
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            return new PreparedImage(tempPath, isTemp: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Vision($"[Preprocess] WIC failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name} — no preview available");
            return null;
        }
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

        // If the thread cannot be started (resource exhaustion), resolve the TCS
        // immediately so the caller never awaits a task that will never complete.
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }
}
