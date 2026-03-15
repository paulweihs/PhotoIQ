using Xunit;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Tests;

public class MediaFileTests
{
    [Fact]
    public void NewMediaFile_HasDefaults()
    {
        var mf = new MediaFile { FilePath = "/test.jpg", FileName = "test.jpg", Extension = ".jpg" };
        Assert.NotEqual(Guid.Empty, mf.Id);
        Assert.Equal(0, mf.Rating);
        Assert.False(mf.IsFavorite);
    }
}
