using System.IO;
using Xunit;
using PhotoWell.Core.Models;
using PhotoWell.Common;

namespace PhotoWell.Tests;

// ── INotifyPropertyChanged ────────────────────────────────────────────────────

/// <summary>Tests for MediaFile INotifyPropertyChanged implementation.</summary>
public class MediaFilePropertyChangedTests
{
    private static MediaFile MakeFile() => new()
    {
        FilePath  = "/test/photo.jpg",
        FileName  = "photo.jpg",
        Extension = ".jpg",
    };

    // Thumbnail setters always raise (no equality guard — by design for UI refresh)
    [Fact]
    public void ThumbnailSmall_Set_RaisesPropertyChanged()
    {
        var mf = MakeFile();
        string? raised = null;
        mf.PropertyChanged += (_, e) => raised = e.PropertyName;
        mf.ThumbnailSmall = "/thumbs/sm.jpg";
        Assert.Equal(nameof(MediaFile.ThumbnailSmall), raised);
    }

    [Fact]
    public void ThumbnailMedium_Set_RaisesPropertyChanged()
    {
        var mf = MakeFile();
        string? raised = null;
        mf.PropertyChanged += (_, e) => raised = e.PropertyName;
        mf.ThumbnailMedium = "/thumbs/md.jpg";
        Assert.Equal(nameof(MediaFile.ThumbnailMedium), raised);
    }

    [Fact]
    public void ThumbnailLarge_Set_RaisesPropertyChanged()
    {
        var mf = MakeFile();
        string? raised = null;
        mf.PropertyChanged += (_, e) => raised = e.PropertyName;
        mf.ThumbnailLarge = "/thumbs/lg.jpg";
        Assert.Equal(nameof(MediaFile.ThumbnailLarge), raised);
    }

    [Fact]
    public void ThumbnailSmall_SameValue_StillRaisesPropertyChanged_NoEqualityGuard()
    {
        // "Always raises — no equality guard" (XML doc comment on MediaFile)
        var mf = MakeFile();
        mf.ThumbnailSmall = "/path.jpg";
        int count = 0;
        mf.PropertyChanged += (_, _) => count++;
        mf.ThumbnailSmall = "/path.jpg"; // same value again
        Assert.Equal(1, count);          // must still raise
    }

    // IsOffline — has an explicit equality guard: "if (_isOffline == value) return"
    [Fact]
    public void IsOffline_NewValue_RaisesPropertyChanged()
    {
        var mf = MakeFile(); // IsOffline defaults to false
        string? raised = null;
        mf.PropertyChanged += (_, e) => raised = e.PropertyName;
        mf.IsOffline = true;
        Assert.Equal(nameof(MediaFile.IsOffline), raised);
    }

    [Fact]
    public void IsOffline_SameValue_DoesNotRaisePropertyChanged()
    {
        var mf = MakeFile(); // IsOffline = false (default)
        int count = 0;
        mf.PropertyChanged += (_, _) => count++;
        mf.IsOffline = false; // same value — equality guard suppresses the event
        Assert.Equal(0, count);
    }

    // LoadedThumbnail — reference-equality guard: "if (ReferenceEquals(...)) return"
    [Fact]
    public void LoadedThumbnail_NewReference_RaisesPropertyChanged()
    {
        var mf = MakeFile();
        string? raised = null;
        mf.PropertyChanged += (_, e) => raised = e.PropertyName;
        mf.LoadedThumbnail = new object();
        Assert.Equal(nameof(MediaFile.LoadedThumbnail), raised);
    }

    [Fact]
    public void LoadedThumbnail_SameReference_DoesNotRaisePropertyChanged()
    {
        var mf  = MakeFile();
        var obj = new object();
        mf.LoadedThumbnail = obj;
        int count = 0;
        mf.PropertyChanged += (_, _) => count++;
        mf.LoadedThumbnail = obj; // same reference — no event
        Assert.Equal(0, count);
    }

    [Fact]
    public void LoadedThumbnail_DifferentReference_RaisesPropertyChanged()
    {
        var mf = MakeFile();
        mf.LoadedThumbnail = new object();
        int count = 0;
        mf.PropertyChanged += (_, _) => count++;
        mf.LoadedThumbnail = new object(); // different reference
        Assert.Equal(1, count);
    }
}

// ── Default values ────────────────────────────────────────────────────────────

/// <summary>Tests for MediaFile default property values on initialization.</summary>
public class MediaFileDefaultValueTests
{
    private static MediaFile New() => new() { FilePath = "f", FileName = "f", Extension = ".jpg" };

    [Fact] public void AnalysisStatus_DefaultIsPending()    => Assert.Equal(AnalysisStatus.Pending, New().AnalysisStatus);
    [Fact] public void MediaType_DefaultIsPhoto()           => Assert.Equal(MediaType.Photo,        New().MediaType);
    [Fact] public void IsFavorite_DefaultIsFalse()          => Assert.False(New().IsFavorite);
    [Fact] public void IsRejected_DefaultIsFalse()          => Assert.False(New().IsRejected);
    [Fact] public void IsExcluded_DefaultIsFalse()          => Assert.False(New().IsExcluded);
    [Fact] public void IsAnalyzed_DefaultIsFalse()          => Assert.False(New().IsAnalyzed);
    [Fact] public void IsOffline_DefaultIsFalse()           => Assert.False(New().IsOffline);
    [Fact] public void Rating_DefaultIsZero()               => Assert.Equal(0, New().Rating);

    [Fact]
    public void Id_AutoGeneratedAndNotEmpty()
        => Assert.NotEqual(Guid.Empty, New().Id);

    [Fact]
    public void TwoInstances_HaveDifferentIds()
        => Assert.NotEqual(New().Id, New().Id);

    [Fact]
    public void Tags_InitialisedToEmptyCollection()
    {
        var mf = New();
        Assert.NotNull(mf.Tags);
        Assert.Empty(mf.Tags);
    }

    [Fact]
    public void Albums_InitialisedToEmptyCollection()
    {
        var mf = New();
        Assert.NotNull(mf.Albums);
        Assert.Empty(mf.Albums);
    }

    [Fact]
    public void Faces_InitialisedToEmptyCollection()
    {
        var mf = New();
        Assert.NotNull(mf.Faces);
        Assert.Empty(mf.Faces);
    }

    [Fact]
    public void DateImported_DefaultIsRecentUtcTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var mf     = New();
        var after  = DateTime.UtcNow.AddSeconds(2);
        Assert.InRange(mf.DateImported, before, after);
    }

    [Fact]
    public void AiDescription_DefaultIsNull()
        => Assert.Null(New().AiDescription);

    [Fact]
    public void UserCaption_DefaultIsNull()
        => Assert.Null(New().UserCaption);
}

// ── Derived / computed properties ─────────────────────────────────────────────

/// <summary>Tests for MediaFile computed/derived properties (IsVisionPending, OfflineDriveRoot).</summary>
public class MediaFileDerivedPropertyTests
{
    [Theory]
    [InlineData(AnalysisStatus.VisionPending, true)]
    [InlineData(AnalysisStatus.Pending,       false)]
    [InlineData(AnalysisStatus.Processing,    false)]
    [InlineData(AnalysisStatus.Complete,      false)]
    [InlineData(AnalysisStatus.Failed,        false)]
    public void IsVisionPending_DerivedFromAnalysisStatus(AnalysisStatus status, bool expected)
    {
        var mf = new MediaFile { FilePath = "f", FileName = "f", Extension = ".jpg", AnalysisStatus = status };
        Assert.Equal(expected, mf.IsVisionPending);
    }

    [Theory]
    [InlineData(@"C:\Photos\img.jpg",    "C:")]
    [InlineData(@"E:\Vacation\pic.jpg",  "E:")]
    [InlineData(@"D:\img.jpg",           "D:")]
    public void OfflineDriveRoot_ExtractsDriveLetter(string path, string expectedRoot)
    {
        var mf = new MediaFile { FilePath = path, FileName = Path.GetFileName(path), Extension = ".jpg" };
        Assert.Equal(expectedRoot, mf.OfflineDriveRoot);
    }
}

// ── AnalysisStatus / MediaType enum contract ──────────────────────────────────

/// <summary>Tests for AnalysisStatus and MediaType enum values and contracts.</summary>
public class PhotoEnumTests
{
    [Fact]
    public void AnalysisStatus_Pending_IsZero()
        => Assert.Equal(0, (int)AnalysisStatus.Pending);

    [Fact]
    public void AnalysisStatus_Processing_IsOne()
        => Assert.Equal(1, (int)AnalysisStatus.Processing);

    [Fact]
    public void AnalysisStatus_HasAllFiveValues()
    {
        var values = Enum.GetValues<AnalysisStatus>();
        Assert.Contains(AnalysisStatus.Pending,       values);
        Assert.Contains(AnalysisStatus.Processing,    values);
        Assert.Contains(AnalysisStatus.Complete,      values);
        Assert.Contains(AnalysisStatus.Failed,        values);
        Assert.Contains(AnalysisStatus.VisionPending, values);
        Assert.Equal(5, values.Length);
    }

    [Fact]
    public void MediaType_HasPhotoVideoRaw()
    {
        var values = Enum.GetValues<MediaType>();
        Assert.Contains(MediaType.Photo, values);
        Assert.Contains(MediaType.Video, values);
        Assert.Contains(MediaType.Raw,   values);
        Assert.Equal(3, values.Length);
    }
}

// ── AppSettings constants guard ───────────────────────────────────────────────

/// <summary>Tests for AppSettings constants and vision analysis prompts.</summary>
public class AppSettingsConstantsTests
{
    [Fact]
    public void Version_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppSettings.Version));

    [Fact]
    public void Version_HasThreePartSemanticFormat()
    {
        var parts = AppSettings.Version.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _), $"'{p}' is not numeric"));
    }

    [Fact]
    public void ExpressLibraryLimit_Is25000()
        => Assert.Equal(25_000, AppSettings.ExpressLibraryLimit);

    [Fact]
    public void CurrentPromptVersion_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppSettings.CurrentPromptVersion));

    [Fact]
    public void CurrentPromptVersion_StartsWithLowercaseV()
        => Assert.StartsWith("v", AppSettings.CurrentPromptVersion);

    [Fact]
    public void VisionAnalysisPrompt_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppSettings.VisionAnalysisPrompt));

    [Fact]
    public void VisionAnalysisPrompt_InstructsGenderNeutralLanguage()
        => Assert.Contains("gender-neutral", AppSettings.VisionAnalysisPrompt, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void VisionAnalysisPrompt_BansGenderedTerms()
    {
        // Prompt must explicitly prohibit gendered nouns to guide the model
        var prompt = AppSettings.VisionAnalysisPrompt;
        Assert.Contains("man", prompt,  StringComparison.OrdinalIgnoreCase);
        Assert.Contains("woman", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OllamaBaseUrl_PointsToLocalhost()
    {
        Assert.StartsWith("http://", AppSettings.OllamaBaseUrl);
        Assert.Contains("127.0.0.1", AppSettings.OllamaBaseUrl);
    }

    [Fact]
    public void ModelDownloadBaseUrl_EndsWithSlash()
        => Assert.EndsWith("/", AppSettings.ModelDownloadBaseUrl);

    [Fact]
    public void VisionModelName_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppSettings.VisionModelName));

    [Fact]
    public void DatabasePath_EndsWithDbExtension()
        => Assert.EndsWith(".db", AppSettings.DatabasePath, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ThumbnailsPath_ContainsAppName()
        => Assert.Contains("PhotoWell", AppSettings.ThumbnailsPath, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ModelsPath_ContainsAppName()
        => Assert.Contains("PhotoWell", AppSettings.ModelsPath, StringComparison.OrdinalIgnoreCase);
}
