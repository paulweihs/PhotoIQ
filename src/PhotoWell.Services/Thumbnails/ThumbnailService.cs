using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Sdcb.LibRaw;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Services.Import;

namespace PhotoWell.Services.Thumbnails;

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

    /// <summary>
    /// Initializes a new instance of the ThumbnailService.
    /// </summary>
    /// <remarks>
    /// The service generates and caches thumbnail images at three standard sizes: 150px (Small), 400px (Medium),
    /// and 800px (Large). Thumbnails are stored in the LocalApplicationData directory by default
    /// (%LOCALAPPDATA%\PhotoWell\thumbnails\) with a two-character subdirectory for hashing.
    /// </remarks>
    /// <param name="basePath">Optional base path for thumbnail storage. If null, defaults to LocalApplicationData\PhotoWell\thumbnails.</param>
    public ThumbnailService(string? basePath = null) => _basePath = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWell", "thumbnails");

    /// <summary>
    /// Generates and caches thumbnail images at all three standard sizes for a media file.
    /// </summary>
    /// <remarks>
    /// This method handles both standard image formats (JPEG, PNG, GIF, TIFF) via ImageSharp, and proprietary
    /// RAW formats (CR3, ARW, DNG, etc.) via LibRaw with Windows Imaging Component (WIC) fallback.
    ///
    /// The process includes:
    /// 1. Loading the image (raw or standard format)
    /// 2. Reading and applying EXIF Orientation tag (uses MetadataExtractor for non-RAW to handle legacy encodings)
    /// 3. Capturing image dimensions in the MediaFile
    /// 4. Generating three JPEG thumbnails: 150px (Small), 400px (Medium), 800px (Large)
    ///
    /// Paths are stored in MediaFile.ThumbnailSmall/Medium/Large for later retrieval via GetThumbnailPath().
    /// </remarks>
    /// <param name="mf">The MediaFile to generate thumbnails for. Dimensions and thumbnail paths will be populated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ThumbnailResult with Success=true if all thumbnails were generated; false if an error occurred.</returns>
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
                // For non-RAW files we use MetadataExtractor to read the orientation tag,
                // which is more reliable than ImageSharp's built-in AutoOrient() for old
                // camera files (e.g. Canon EOS 10D 2005-era JPEGs with EXIF 2.2 encoding).
                // If MetadataExtractor finds no tag we fall back to AutoOrient().
                if (!RawExts.Contains(ext))
                {
                    var orientation = ReadJpegExifOrientation(mf.FilePath);
                    if (orientation != 1)
                        ApplyExifOrientation(img, orientation);
                    else
                        img.Mutate(x => x.AutoOrient()); // covers cases MetadataExtractor misses
                }
                else
                {
                    // RAW: LibRaw already applies rotation during demosaic.
                    // AutoOrient() catches any residual EXIF in the preview image.
                    img.Mutate(x => x.AutoOrient());
                }
                // Apply user-requested rotation on top of EXIF orientation.
                if (mf.UserRotation != 0)
                    img.Mutate(x => x.Rotate(mf.UserRotation));

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

    /// <summary>
    /// Resolves the full file path for a thumbnail image based on media file ID and size.
    /// </summary>
    /// <remarks>
    /// Thumbnails are stored with a two-character subdirectory hierarchy for load distribution.
    /// For example: C:\...\photowell\thumbnails\ab\small\abcdef....jpg
    /// This method constructs the expected path; it does not verify that the file exists.
    /// </remarks>
    /// <param name="id">The MediaFile ID (GUID).</param>
    /// <param name="size">The thumbnail size (Small=150px, Medium=400px, Large=800px).</param>
    /// <returns>Full file path where the thumbnail is (or should be) stored.</returns>
    public string GetThumbnailPath(Guid id, ThumbnailSize size) => Path.Combine(_basePath, id.ToString("N")[..2], size.ToString().ToLower(), $"{id:N}.jpg");

    private async Task<string> GenThumb(Image img, Guid id, ThumbnailSize size, CancellationToken ct)
    {
        var path = GetThumbnailPath(id, size);
        var dir = Path.GetDirectoryName(path);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);
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
    /// Reads the EXIF Orientation tag from a JPEG (or any MetadataExtractor-supported format).
    /// Returns 1 (normal) if no orientation tag is found or on any error.
    /// Values: 1=normal, 2=flip-H, 3=180°, 4=flip-V, 5=transpose, 6=90°CW, 7=transverse, 8=270°CW.
    /// </summary>
    private static ushort ReadJpegExifOrientation(string filePath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(filePath);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o >= 1 && o <= 8)
                return (ushort)o;
        }
        catch { /* treat as normal orientation */ }
        return 1;
    }

    /// <summary>
    /// Applies an EXIF orientation value to an ImageSharp image in-place,
    /// then resets the EXIF orientation tag to 1 (normal) so that downstream
    /// decoders (WPF BitmapImage, browsers, etc.) don't rotate the pixels again.
    /// Maps EXIF Orientation 1–8 to the correct combination of rotation and flip.
    /// </summary>
    private static void ApplyExifOrientation(Image img, ushort orientation)
    {
        img.Mutate(ctx =>
        {
            switch (orientation)
            {
                case 2: ctx.Flip(FlipMode.Horizontal); break;
                case 3: ctx.Rotate(RotateMode.Rotate180); break;
                case 4: ctx.Flip(FlipMode.Vertical); break;
                case 5: ctx.Rotate(RotateMode.Rotate90); ctx.Flip(FlipMode.Horizontal); break;
                case 6: ctx.Rotate(RotateMode.Rotate90); break;
                case 7: ctx.Rotate(RotateMode.Rotate270); ctx.Flip(FlipMode.Horizontal); break;
                case 8: ctx.Rotate(RotateMode.Rotate270); break;
                // case 1 and anything else: no-op
            }
        });
        // After baking the rotation into pixels, mark the saved file as "normal" orientation.
        // Without this, decoders that auto-apply EXIF (WPF BitmapImage, Chrome, etc.) would
        // rotate the already-rotated pixels a second time, producing a double-rotation.
        img.Metadata.ExifProfile?.SetValue(
            SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)1);
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
