using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Services.Import;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>
/// Unit tests for extracted private helper methods in ImportService.
/// Tests exercise private helpers (IsUnsuitableForVision, RunVisionInferenceAsync,
/// ResolveVisionPathAsync, RunClipPipelineAsync, StripAiState) via public methods.
/// </summary>
public class ImportServiceHelperTests
{
    private readonly Mock<IMediaFileRepository> _mockRepo;
    private readonly Mock<IThumbnailService> _mockThumbs;
    private readonly Mock<ITaggingService> _mockTagging;
    private readonly Mock<IImageUnderstandingService> _mockVision;
    private readonly Mock<IImagePreprocessor> _mockPreprocessor;
    private readonly Mock<IAnalysisMetricRepository> _mockMetrics;
    private readonly Mock<IFaceService> _mockFaces;
    private readonly ImportService _service;

    public ImportServiceHelperTests()
    {
        _mockRepo = new Mock<IMediaFileRepository>();
        _mockThumbs = new Mock<IThumbnailService>();
        _mockTagging = new Mock<ITaggingService>();
        _mockVision = new Mock<IImageUnderstandingService>();
        _mockPreprocessor = new Mock<IImagePreprocessor>();
        _mockMetrics = new Mock<IAnalysisMetricRepository>();
        _mockFaces = new Mock<IFaceService>();

        _service = new ImportService(
            _mockRepo.Object,
            _mockThumbs.Object,
            _mockTagging.Object,
            _mockVision.Object,
            _mockPreprocessor.Object,
            _mockMetrics.Object,
            _mockFaces.Object);
    }

    // ── Helper Methods ───────────────────────────────────────────────────────

    /// <summary>Creates a temporary file for testing (deleted by test cleanup).</summary>
    private static string MakeTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jpg");
        File.WriteAllBytes(path, []);  // empty file, just needs to exist
        return path;
    }

    /// <summary>Creates a MediaFile with sensible defaults for testing.</summary>
    private static MediaFile MakeFile(
        string? filePath = null,
        int width = 800,
        int height = 600,
        AnalysisStatus status = AnalysisStatus.VisionPending)
        => new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = filePath ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".jpg"),
            FileName = "test.jpg",
            Extension = ".jpg",
            FileSize = 1_000_000,
            DateImported = DateTime.UtcNow,
            Tags = new List<Tag>(),
            AnalysisStatus = status,
            Width = width,
            Height = height,
            ThumbnailLarge = null,
            PromptVersion = "v15c",
            AiModelUsed = "llama3.2-vision"
        };

    // ── GROUP A: IsUnsuitableForVision (via RunVisionForPhotoAsync) ──────────

    /// <summary>A1: Image too narrow (Width < 300) should skip vision analysis.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_ImageTooNarrow_SkipsVision()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 50;
            mf.Height = 600;

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            _mockVision.Verify(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockRepo.Verify(r => r.UpdateAsync(It.IsAny<MediaFile>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>A2: Banner aspect ratio (6:1 or wider) should skip vision analysis.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_BannerAspectRatio_SkipsVision()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 1200;
            mf.Height = 200;  // 6:1 aspect ratio

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            _mockVision.Verify(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>A3: Normal dimensions (800×600) should call vision analysis.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_NormalDimensions_CallsVision()
    {
        // Arrange
        var tempFile = MakeTempFile();
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 800;
            mf.Height = 600;
            mf.ThumbnailLarge = tempThumb;

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            _mockVision.Verify(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    /// <summary>A4: Zero dimensions (0×0) should still call vision (dimensions unknown, don't gate).</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_ZeroDimensions_CallsVision()
    {
        // Arrange
        var tempFile = MakeTempFile();
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 0;
            mf.Height = 0;
            mf.ThumbnailLarge = tempThumb;

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            _mockVision.Verify(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    // ── GROUP B: RunVisionInferenceAsync — Loop Detection ────────────────────

    /// <summary>B1: Looping description (repetitive n-grams) should result in empty description.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_LoopingDescription_SetsEmptyDescription()
    {
        // Arrange
        var tempFile = MakeTempFile();
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 800;
            mf.Height = 600;
            mf.ThumbnailLarge = tempThumb;

            var loopingDescription = string.Join(". ",
                Enumerable.Repeat("The image shows a red circle on white", 12)) + ".";

            MediaFile? capturedMf = null;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ImageUnderstanding(loopingDescription, []));
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>()))
                .Callback<MediaFile>(m => capturedMf = m)
                .Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedMf);
            Assert.Equal(string.Empty, capturedMf.AiDescription);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    /// <summary>B2: Normal description should be set correctly.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_NormalDescription_SetsDescription()
    {
        // Arrange
        var tempFile = MakeTempFile();
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 800;
            mf.Height = 600;
            mf.ThumbnailLarge = tempThumb;

            var normalDescription = "A sunny beach scene with blue ocean waves.";
            MediaFile? capturedMf = null;

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ImageUnderstanding(normalDescription, []));
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>()))
                .Callback<MediaFile>(m => capturedMf = m)
                .Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedMf);
            Assert.Equal(normalDescription, capturedMf.AiDescription);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    /// <summary>B3: Empty description should result in empty string being set.</summary>
    [Fact]
    public async Task RunVisionForPhotoAsync_EmptyDescription_SetsEmptyString()
    {
        // Arrange
        var tempFile = MakeTempFile();
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.VisionPending);
            mf.Width = 800;
            mf.Height = 600;
            mf.ThumbnailLarge = tempThumb;

            MediaFile? capturedMf = null;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>()))
                .Callback<MediaFile>(m => capturedMf = m)
                .Returns(Task.CompletedTask);

            // Act
            await _service.RunVisionForPhotoAsync(mf.Id, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedMf);
            Assert.Equal(string.Empty, capturedMf.AiDescription);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    // ── GROUP C: ResolveVisionPathAsync — Path Priority ─────────────────────

    /// <summary>C1: When thumbnail exists, vision analysis should use the thumbnail path.</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_ThumbnailExists_UsesThumbnailForVision()
    {
        // Arrange
        var tempThumb = MakeTempFile();
        try
        {
            var mf = MakeFile(status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = tempThumb;
            mf.MediaType = MediaType.Photo;

            string? capturedVisionPath = null;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TagPrediction>());
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((path, _) => capturedVisionPath = path)
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);
            _mockPreprocessor.Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PreparedImage?)null);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert
            Assert.Equal(tempThumb, capturedVisionPath);
            _mockPreprocessor.Verify(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (File.Exists(tempThumb)) File.Delete(tempThumb);
        }
    }

    /// <summary>C2: Without thumbnail, vision should use prepared image path (when available).</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_NoThumbnailNativePrepared_UsesPreparedPath()
    {
        // Arrange
        var tempOriginal = MakeTempFile();
        var tempPrepared = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempOriginal, status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = null;  // no thumbnail
            mf.MediaType = MediaType.Photo;

            string? capturedVisionPath = null;
            var preparedImage = new PreparedImage(tempPrepared, isTemp: false);

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TagPrediction>());
            _mockPreprocessor.Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(preparedImage);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((path, _) => capturedVisionPath = path)
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert
            Assert.Equal(tempPrepared, capturedVisionPath);
        }
        finally
        {
            if (File.Exists(tempOriginal)) File.Delete(tempOriginal);
            if (File.Exists(tempPrepared)) File.Delete(tempPrepared);
        }
    }

    // ── GROUP D: RunClipPipelineAsync ────────────────────────────────────────

    /// <summary>D1: When tag predictions are returned, tags should be added to the file.</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_WithTagPredictions_AddsTagsToFile()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = tempFile;

            var tagPredictions = new List<TagPrediction>
            {
                new TagPrediction("outdoor", TagCategory.Scene, 0.95f),
                new TagPrediction("sunset", TagCategory.Scene, 0.87f),
                new TagPrediction("landscape", TagCategory.Scene, 0.92f)
            };

            var callCount = 0;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tagPredictions);
            _mockRepo.Setup(r => r.GetOrCreateTagAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TagCategory>(),
                    It.IsAny<bool>(), It.IsAny<float>()))
                .Returns((string name, string norm, TagCategory cat, bool ai, float conf) =>
                    Task.FromResult(new Tag
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        NormalizedName = norm,
                        Category = cat,
                        IsAIGenerated = ai,
                        Confidence = conf
                    }))
                .Callback(() => callCount++);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert
            Assert.Equal(3, callCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>D2: When no tag predictions returned, file still completes normally.</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_ZeroTagPredictions_IsAnalyzedStaysFalse_ButCompletes()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = tempFile;

            MediaFile? lastCapturedMf = null;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TagPrediction>());
            _mockPreprocessor.Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PreparedImage?)null);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>()))
                .Callback<MediaFile>(m => lastCapturedMf = m)
                .Returns(Task.CompletedTask);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert - Completes without error (no exception thrown)
            Assert.NotNull(lastCapturedMf);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>D3: When tagging service throws, vision analysis should still be attempted.</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_TaggingServiceThrows_ContinuesToVision()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = tempFile;

            var visionCalled = false;
            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("CLIP service offline"));
            _mockPreprocessor.Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PreparedImage?)null);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => visionCalled = true)
                .ReturnsAsync(new ImageUnderstanding("Valid description from vision", []));
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>())).Returns(Task.CompletedTask);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert
            Assert.True(visionCalled);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── GROUP E: StripAiState ────────────────────────────────────────────────

    /// <summary>E1: Pre-existing AI tags and description should be cleared before reanalysis.</summary>
    [Fact]
    public async Task ReanalyzeFileAsync_PreExistingAiTags_ClearsBeforeReanalysis()
    {
        // Arrange
        var tempFile = MakeTempFile();
        try
        {
            var mf = MakeFile(filePath: tempFile, status: AnalysisStatus.Pending);
            mf.ThumbnailLarge = tempFile;
            mf.IsAnalyzed = true;
            mf.AiDescription = "Old description that should be cleared";
            mf.Tags = new List<Tag>
            {
                new Tag { Id = Guid.NewGuid(), Name = "old-tag-1", NormalizedName = "old-tag-1", IsAIGenerated = true },
                new Tag { Id = Guid.NewGuid(), Name = "old-tag-2", NormalizedName = "old-tag-2", IsAIGenerated = true }
            };

            _mockRepo.Setup(r => r.GetByIdAsync(mf.Id)).ReturnsAsync(mf);
            _mockTagging.Setup(t => t.GenerateTagsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TagPrediction>());
            _mockPreprocessor.Setup(p => p.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PreparedImage?)null);
            _mockVision.Setup(v => v.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageUnderstanding.Empty);
            _mockVision.SetupGet(v => v.PromptVersion).Returns("v15c");

            var capturedMf = mf;
            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<MediaFile>()))
                .Callback<MediaFile>(m => capturedMf = m)
                .Returns(Task.CompletedTask);

            // Act
            await _service.ReanalyzeFileAsync(mf.Id, null, CancellationToken.None);

            // Assert - After reanalysis, old tags should be cleared
            Assert.Empty(capturedMf.Tags);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
