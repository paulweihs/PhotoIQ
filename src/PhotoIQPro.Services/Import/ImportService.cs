using System.Security.Cryptography;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Import;

public class ImportService : IImportService
{
    private readonly IMediaFileRepository _repo;
    private readonly IThumbnailService _thumbs;
    private readonly ITaggingService _tagging;
    private readonly IImageUnderstandingService _vision;
    private readonly IImagePreprocessor _preprocessor;
    private static readonly HashSet<string> PhotoExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".heic" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".avi", ".mkv", ".wmv" };
    private static readonly HashSet<string> RawExts = new(StringComparer.OrdinalIgnoreCase) { ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng" };

    public ImportService(IMediaFileRepository repo, IThumbnailService thumbs, ITaggingService tagging, IImageUnderstandingService vision, IImagePreprocessor preprocessor)
    {
        _repo = repo;
        _thumbs = thumbs;
        _tagging = tagging;
        _vision = vision;
        _preprocessor = preprocessor;
    }

    public bool IsSupportedFile(string path) => PhotoExts.Contains(Path.GetExtension(path)) || VideoExts.Contains(Path.GetExtension(path)) || RawExts.Contains(Path.GetExtension(path));

    public async Task<MediaFile?> ImportFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath) || !IsSupportedFile(filePath) || await _repo.ExistsAsync(filePath)) return null;
        var fi = new FileInfo(filePath);
        var ext = fi.Extension.ToLowerInvariant();
        var mf = new MediaFile { FilePath = filePath, FileName = fi.Name, Extension = ext, FileSize = fi.Length, MediaType = VideoExts.Contains(ext) ? MediaType.Video : RawExts.Contains(ext) ? MediaType.Raw : MediaType.Photo };
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(filePath)) mf.FileHash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));
        if (mf.MediaType != MediaType.Video) ExtractMetadata(mf, filePath);
        await _repo.AddAsync(mf);
        await _thumbs.GenerateThumbnailsAsync(mf, ct);

        if (_tagging.IsAvailable)
        {
            try
            {
                var predictions = await _tagging.GenerateTagsAsync(mf.FilePath, ct);
                foreach (var p in predictions)
                    mf.Tags.Add(new PhotoIQPro.Core.Models.Tag
                    {
                        Name = p.Label,
                        NormalizedName = p.Label.ToLowerInvariant(),
                        Category = p.Category,
                        IsAIGenerated = true,
                        Confidence = p.Confidence
                    });
                mf.IsAnalyzed = true;
                mf.DateAnalyzed = DateTime.UtcNow;
            }
            catch { /* tagging failure must not abort the import */ }
        }

        // LLaVA understanding — generates a natural-language description and additional tags.
        // Non-JPEG/PNG files are converted to a temp JPEG first; the temp is deleted after.
        if (mf.MediaType == MediaType.Photo)
        {
            try
            {
                using var prepared = await _preprocessor.PrepareAsync(mf.FilePath, ct);
                if (prepared != null)
                {
                    var understanding = await _vision.AnalyzeImageAsync(prepared.Path, ct);
                    if (!string.IsNullOrEmpty(understanding.Description))
                        mf.AiDescription = understanding.Description;
                    foreach (var label in understanding.Tags)
                        mf.Tags.Add(new PhotoIQPro.Core.Models.Tag
                        {
                            Name = label,
                            NormalizedName = label,
                            Category = PhotoIQPro.Core.Models.TagCategory.General,
                            IsAIGenerated = true
                        });
                    if (understanding.Tags.Count > 0)
                    {
                        mf.IsAnalyzed = true;
                        mf.DateAnalyzed = DateTime.UtcNow;
                    }
                }
            }
            catch { /* vision failure must not abort the import */ }
        }

        await _repo.UpdateAsync(mf);
        return mf;
    }

    public async Task<ImportResult> ImportFolderAsync(string folder, bool recursive, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var errors = new List<string>();
        var files = System.IO.Directory.GetFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(IsSupportedFile).ToList();
        int total = files.Count, processed = 0, imported = 0, skipped = 0, failed = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try { var r = await ImportFileAsync(f, ct); if (r != null) imported++; else skipped++; }
            catch (Exception ex) { failed++; errors.Add($"{f}: {ex.Message}"); }
            processed++;
            progress?.Report(new ImportProgress(total, processed, imported, skipped, failed, f));
        }
        return new ImportResult(total, imported, skipped, failed, DateTime.UtcNow - start, errors);
    }

    private static void ExtractMetadata(MediaFile mf, string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null)
            {
                mf.CameraMake = ifd0.GetDescription(ExifDirectoryBase.TagMake);
                mf.CameraModel = ifd0.GetDescription(ExifDirectoryBase.TagModel);
            }

            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (sub != null)
            {
                if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                    mf.DateTaken = dt;
            }

            // GPS extraction removed - MetadataExtractor 2.9 changed the API
            // Can be added back later
        }
        catch { }
    }
}
