using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Thumbnails;

public class ThumbnailService : IThumbnailService
{
    private readonly string _basePath;
    public ThumbnailService(string? basePath = null) => _basePath = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro", "thumbnails");

    public async Task<ThumbnailResult> GenerateThumbnailsAsync(MediaFile mf, CancellationToken ct = default)
    {
        try
        {
            if (mf.MediaType == MediaType.Video) return new ThumbnailResult(false, null, null, null, "Video thumbnails not implemented");
            using var img = await Image.LoadAsync(mf.FilePath, ct);
            if (mf.Width == 0) mf.Width = img.Width;
            if (mf.Height == 0) mf.Height = img.Height;
            var s = await GenThumb(img, mf.Id, ThumbnailSize.Small, ct);
            var m = await GenThumb(img, mf.Id, ThumbnailSize.Medium, ct);
            var l = await GenThumb(img, mf.Id, ThumbnailSize.Large, ct);
            mf.ThumbnailSmall = s; mf.ThumbnailMedium = m; mf.ThumbnailLarge = l;
            return new ThumbnailResult(true, s, m, l, null);
        }
        catch (Exception ex) { return new ThumbnailResult(false, null, null, null, ex.Message); }
    }

    public string GetThumbnailPath(Guid id, ThumbnailSize size) => Path.Combine(_basePath, id.ToString("N")[..2], size.ToString().ToLower(), $"{id:N}.jpg");

    private async Task<string> GenThumb(Image img, Guid id, ThumbnailSize size, CancellationToken ct)
    {
        var path = GetThumbnailPath(id, size);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var thumb = img.Clone(x => x.Resize(new ResizeOptions { Size = new Size((int)size, (int)size), Mode = ResizeMode.Max }));
        await thumb.SaveAsJpegAsync(path, ct);
        return path;
    }
}
