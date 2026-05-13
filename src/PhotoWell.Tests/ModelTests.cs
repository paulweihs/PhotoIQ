using Xunit;
using PhotoWell.Core.Models;

namespace PhotoWell.Tests;

/// <summary>Tests for the MediaFile model: defaults and initialization.</summary>
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
