using Sdcb.LibRaw;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Services.Import;

namespace PhotoIQPro.Services.Thumbnails;

public class ThumbnailService : IThumbnailService
{
    private readonly string _basePath;

    // Proprietary camera RAW formats that ImageSharp cannot decode correctly.
    // These go through RawPreviewExtractor → LibRaw → WIC → ImageSharp fallback.
    // Covers all formats recognised by LibRaw; excludes standard formats (.png, .tif, .tiff,
    // .exr, .pfm) that ImageSharp handles natively.
    private static readonly HashSet<string> RawExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr",               // Hasselblad
        ".ari",               // ARRI
        ".arw", ".sr2", ".srf",  // Sony
        ".bay",               // Casio
        ".bmq",               // Blah
        ".cap", ".iiq",       // Phase One
        ".cine",              // Phantom
        ".cr2", ".cr3", ".crw",  // Canon
        ".cs1",               // Sinar CS1
        ".dc2", ".dcr", ".k25", ".kc2", ".kdc",  // Kodak
        ".dng",               // Adobe DNG (used by many manufacturers)
        ".erf",               // Epson
        ".fff",               // Hasselblad/Imacon
        ".gpr",               // GoPro
        ".ia",                // Sinar
        ".mdc",               // Minolta/Agfa
        ".mef",               // Mamiya
        ".mos",               // Leaf
        ".mrw",               // Minolta/Konica Minolta
        ".nef", ".nrw",       // Nikon
        ".orf",               // Olympus
        ".pef", ".ptx",       // Pentax
        ".pxn",               // Logitech
        ".qtk",               // Apple QuickTake
        ".raf",               // Fujifilm
        ".raw", ".rw2",       // Panasonic (and generic)
        ".rdc",               // Ricoh
        ".rw1",               // Leica
        ".srw",               // Samsung
        ".sti",               // Sinar
        ".x3f",               // Sigma
    };

    public ThumbnailService(string? basePath = null) => _basePath = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro", "thumbnails");

    public async Task<ThumbnailResult> GenerateThumbnailsAsync(MediaFile mf, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(mf.FilePath);
            Image img;
            if (RawExts.Contains(ext))
            {
                img = await LoadRawImageAsync(mf.FilePath, mf.FileName, ct);
            }
            else
            {
                img = await Image.LoadAsync(mf.FilePath, ct);
            }

            using (img)
            {
                // Apply the EXIF Orientation tag before anything else.
                // Cameras/phones store rotation in metadata rather than rotating pixels;
                // without this, portrait-mode photos load sideways or upside-down.
                img.Mutate(x => x.AutoOrient());
                mf.Width  = img.Width;
                mf.Height = img.Height;
                var s = await GenThumb(img, mf.Id, ThumbnailSize.Small, ct);
                var m = await GenThumb(img, mf.Id, ThumbnailSize.Medium, ct);
                var l = await GenThumb(img, mf.Id, ThumbnailSize.Large, ct);
                mf.ThumbnailSmall = s; mf.ThumbnailMedium = m; mf.ThumbnailLarge = l;
                return new ThumbnailResult(true, s, m, l, null);
            }
        }
        catch (Exception ex) { return new ThumbnailResult(false, null, null, null, ex.Message); }
    }

    public string GetThumbnailPath(Guid id, ThumbnailSize size) => Path.Combine(_basePath, id.ToString("N")[..2], size.ToString().ToLower(), $"{id:N}.jpg");

    private async Task<string> GenThumb(Image img, Guid id, ThumbnailSize size, CancellationToken ct)
    {
        var path = GetThumbnailPath(id, size);
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        using var thumb = img.Clone(x => x.Resize(new ResizeOptions { Size = new Size((int)size, (int)size), Mode = ResizeMode.Max }));
        await thumb.SaveAsJpegAsync(path, ct);
        return path;
    }

    // Minimum short-edge size that counts as a real preview (not just an IFD0 thumbnail).
    private const int MinPreviewEdge = 500;

    /// <summary>
    /// Decodes a RAW/DNG/CR2/NEF/ARW file using the best available method:
    ///   1. TIFF embedded JPEG preview (if ≥ MinPreviewEdge px — avoids IFD0 thumbnails)
    ///   2. Magick.NET + libraw (decodes RAW sensor data; works for all TIFF-based RAW)
    ///   3. WPF WIC (if Windows Camera RAW codec is installed)
    ///   4. ImageSharp fallback (may return the tiny embedded thumbnail)
    /// </summary>
    private static async Task<Image> LoadRawImageAsync(string filePath, string fileName, CancellationToken ct)
    {
        // Priority 1 — extract embedded JPEG preview from TIFF structure.
        // Only accept it if it's large enough to not be the IFD0 thumbnail.
        var previewBytes = RawPreviewExtractor.ExtractLargestJpegPreview(filePath);
        if (previewBytes != null)
        {
            try
            {
                using var ms  = new MemoryStream(previewBytes);
                var preview   = await Image.LoadAsync(ms, ct);
                if (Math.Min(preview.Width, preview.Height) >= MinPreviewEdge)
                {
                    AppLog.Vision($"ThumbnailService: embedded preview {preview.Width}×{preview.Height} from {fileName}");
                    return preview;
                }
                preview.Dispose();
                AppLog.Vision($"ThumbnailService: embedded preview too small ({preview.Width}×{preview.Height}), falling through for {fileName}");
            }
            catch { /* fall through */ }
        }

        // Priority 2 — LibRaw demosaics the RAW sensor data.
        // Works without any OS codec installation.
        var libraw = await LoadViaLibRawAsync(filePath, ct);
        if (libraw != null)
        {
            AppLog.Vision($"ThumbnailService: LibRaw decoded {fileName} at {libraw.Width}×{libraw.Height}");
            return libraw;
        }

        // Priority 3 — WIC (requires Windows Camera RAW codec from the Store).
        var wic = await LoadRawViaWicAsync(filePath, ct);
        if (wic != null)
        {
            AppLog.Vision($"ThumbnailService: WIC decoded {fileName} at {wic.Width}×{wic.Height}");
            return wic;
        }

        // Priority 4 — ImageSharp fallback. Returns the embedded thumbnail if nothing else works.
        AppLog.Vision($"ThumbnailService: all RAW paths failed for {fileName}, using ImageSharp (may be low-res)");
        return await Image.LoadAsync(filePath, ct);
    }

    /// <summary>
    /// Decodes a RAW file via Sdcb.LibRaw (native libraw).
    /// Handles DNG, CR2, NEF, ARW and others without any OS codec.
    /// Returns null on failure.
    /// </summary>
    private static async Task<Image?> LoadViaLibRawAsync(string filePath, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var ctx = RawContext.OpenFile(filePath);
                ctx.Unpack();
                ctx.DcrawProcess(p =>
                {
                    p.UseCameraWb  = true; // apply the WB the camera recorded
                    p.OutputBps    = 8;    // 8-bit sRGB output
                });
                using var processed = ctx.MakeDcrawMemoryImage();
                // LibRaw outputs RGB24 — do NOT call SwapRGB() here.
                // SwapRGB() converts RGB→BGR for GDI+ Bitmaps; ImageSharp Rgb24 expects RGB.
                return (Image)Image.LoadPixelData<Rgb24>(
                    processed.AsSpan<byte>(), processed.Width, processed.Height);
            }, ct);
        }
        catch { return null; }
    }

    /// <summary>
    /// Decodes a RAW/DNG file via WPF WIC, returning the full-resolution demosaiced image.
    /// Requires the Windows Camera RAW codec (available from the Microsoft Store).
    /// Returns null if the codec is missing or the file cannot be decoded.
    /// </summary>
    private static Task<Image?> LoadRawViaWicAsync(string filePath, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Image?>();
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri(filePath, UriKind.Absolute),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                // BMP is lossless — no quality degradation before resize
                var enc = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using var ms = new MemoryStream();
                enc.Save(ms);
                ms.Position = 0;
                tcs.SetResult(Image.Load(ms));
            }
            catch (OperationCanceledException) { tcs.SetCanceled(ct); }
            catch { tcs.SetResult(null); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
