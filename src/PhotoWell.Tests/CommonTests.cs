using Xunit;
using PhotoWell.Common;

namespace PhotoWell.Tests;

/// <summary>Tests for the FormatUtilities.FormatFileSize method.</summary>
public class FormatUtilitiesTests
{
    [Theory]
    [InlineData(0,                  "0 B")]
    [InlineData(1_023,              "1023 B")]
    [InlineData(1_024,              "1.0 KB")]
    [InlineData(1_048_576,          "1.0 MB")]
    [InlineData(1_073_741_824,      "1.0 GB")]
    [InlineData(1_536,              "1.5 KB")]
    public void FormatFileSize_ReturnsExpected(long bytes, string expected)
        => Assert.Equal(expected, FormatUtilities.FormatFileSize(bytes));
}

/// <summary>Tests for DateTime extension methods: GetHolidayName, IsUsaThanksgiving.</summary>
public class DateTimeExtensionsTests
{
    [Theory]
    [InlineData(10, 31, "halloween")]
    [InlineData(10, 30, "halloween")]
    [InlineData(12, 25, "christmas")]
    [InlineData(12, 24, "christmas")]
    [InlineData(12, 26, "christmas")]
    [InlineData(12, 31, "new year")]
    [InlineData(1,   1, "new year")]
    [InlineData(6,  15, null)]
    public void GetHolidayName_ReturnsExpected(int month, int day, string? expected)
        => Assert.Equal(expected, new DateTime(2024, month, day).GetHolidayName());

    [Fact]
    public void GetHolidayName_Thanksgiving_2024()
        => Assert.Equal("thanksgiving", new DateTime(2024, 11, 28).GetHolidayName());

    [Fact]
    public void GetHolidayName_NotThanksgiving_December()
        => Assert.Null(new DateTime(2024, 12, 1).GetHolidayName());

    [Fact]
    public void IsUsaThanksgiving_FourthThursday()
    {
        // 2024: 4th Thursday of November = Nov 28
        Assert.True(new DateTime(2024, 11, 28).IsUsaThanksgiving());
        // Grace period: ±1 day
        Assert.True(new DateTime(2024, 11, 27).IsUsaThanksgiving());
        Assert.True(new DateTime(2024, 11, 29).IsUsaThanksgiving());
        // Outside grace period
        Assert.False(new DateTime(2024, 11, 26).IsUsaThanksgiving());
    }
}

/// <summary>Tests for SupportedFormats extension collections (PhotoExtensions, RawExtensions, etc.).</summary>
public class SupportedFormatsTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".JPG")]
    [InlineData(".heic")]
    [InlineData(".avif")]
    public void PhotoExtensions_ContainsExpected(string ext)
        => Assert.Contains(ext, SupportedFormats.PhotoExtensions);

    [Theory]
    [InlineData(".cr2")]
    [InlineData(".NEF")]
    [InlineData(".dng")]
    [InlineData(".arw")]
    public void RawExtensions_ContainsExpected(string ext)
        => Assert.Contains(ext, SupportedFormats.RawExtensions);

    [Fact]
    public void AllExtensions_IsSupersetOfBoth()
    {
        foreach (var ext in SupportedFormats.PhotoExtensions)
            Assert.Contains(ext, SupportedFormats.AllExtensions);
        foreach (var ext in SupportedFormats.RawExtensions)
            Assert.Contains(ext, SupportedFormats.AllExtensions);
    }

    [Fact]
    public void NativeFormats_SubsetOfPhotoExtensions()
    {
        foreach (var ext in SupportedFormats.NativeFormats)
            Assert.Contains(ext, SupportedFormats.PhotoExtensions);
    }
}
