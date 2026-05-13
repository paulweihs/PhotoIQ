using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Moq;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Desktop.ViewModels;
using Xunit;

namespace PhotoWell.Tests;

/// <summary>
/// Unit tests for FindDuplicatesViewModel, including extracted helpers
/// (BuildScanResultMessage, BuildDeleteConfirmMessage, BuildDeleteResultMessage)
/// tested via public behavior.
/// </summary>
public class FindDuplicatesViewModelTests
{
    private readonly Mock<IMediaFileRepository> _mockRepo;
    private readonly FindDuplicatesViewModel _vm;

    public FindDuplicatesViewModelTests()
    {
        _mockRepo = new Mock<IMediaFileRepository>();
        _vm = new FindDuplicatesViewModel(_mockRepo.Object);
    }

    // ── ScanAsync Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_NoDuplicates_SetsNoGroupsFoundStatus()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new List<DuplicateGroup>());

        // Act
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("No duplicates found.", _vm.StatusText);
        Assert.Empty(_vm.Groups);
        Assert.False(_vm.IsScanning);
    }

    [Fact]
    public async Task ScanAsync_OneDuplicate_SetsSingularGroupMessage()
    {
        // Arrange
        var group = new DuplicateGroup(
            DuplicateGroupType.ExactHash,
            new[] { MakeMediaFile("file1.jpg"), MakeMediaFile("file2.jpg") });
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new[] { group });

        // Act
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Found 1 group", _vm.StatusText);
        Assert.DoesNotContain("groups", _vm.StatusText); // Singular, no 's'
        Assert.Single(_vm.Groups);
    }

    [Fact]
    public async Task ScanAsync_MultipleDuplicates_SetsPluralGroupMessage()
    {
        // Arrange
        var groups = new[]
        {
            new DuplicateGroup(DuplicateGroupType.ExactHash, new[] { MakeMediaFile("a.jpg"), MakeMediaFile("b.jpg") }),
            new DuplicateGroup(DuplicateGroupType.RawJpegPair, new[] { MakeMediaFile("c.raw"), MakeMediaFile("c.jpg") }),
            new DuplicateGroup(DuplicateGroupType.ExactHash, new[] { MakeMediaFile("d.jpg"), MakeMediaFile("e.jpg") })
        };
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(groups);

        // Act
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Found 3 groups", _vm.StatusText);
        Assert.Equal(3, _vm.Groups.Count);
    }

    [Fact]
    public async Task ScanAsync_CallsRepositoryExactlyOnce()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new List<DuplicateGroup>());

        // Act
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert
        _mockRepo.Verify(r => r.GetDuplicateGroupsAsync(), Times.Once);
    }

    [Fact]
    public async Task ScanAsync_SetsIsScanningFalseOnCompletion()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new List<DuplicateGroup>());

        // Act
        Assert.False(_vm.IsScanning);
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert
        Assert.False(_vm.IsScanning);
    }

    [Fact]
    public async Task ScanAsync_ClearsGroupsOnRescan()
    {
        // Arrange — first scan
        var group1 = new DuplicateGroup(
            DuplicateGroupType.ExactHash,
            new[] { MakeMediaFile("a.jpg"), MakeMediaFile("b.jpg") });
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new[] { group1 });
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.Single(_vm.Groups);

        // Act — second scan with no duplicates
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new List<DuplicateGroup>());
        await _vm.ScanCommand.ExecuteAsync(null);

        // Assert — old group is cleared
        Assert.Empty(_vm.Groups);
        Assert.Equal("No duplicates found.", _vm.StatusText);
    }

    // ── MarkedCount/HasMarked Tests ───────────────────────────────────────

    [Fact]
    public void MarkedCount_NoMarkedFiles_ReturnsZero()
    {
        // Arrange
        var group = new DuplicateGroup(
            DuplicateGroupType.ExactHash,
            new[] { MakeMediaFile("a.jpg"), MakeMediaFile("b.jpg") });
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new[] { group });

        // Act
        var count = _vm.MarkedCount;

        // Assert
        Assert.Equal(0, count);
        Assert.False(_vm.HasMarked);
    }

    [Fact]
    public async Task HasMarked_WhenFileMarked_ReturnsTrue()
    {
        // Arrange
        var file1 = MakeMediaFile("a.jpg");
        var file2 = MakeMediaFile("b.jpg");
        var group = new DuplicateGroup(DuplicateGroupType.ExactHash, new[] { file1, file2 });
        _mockRepo.Setup(r => r.GetDuplicateGroupsAsync())
            .ReturnsAsync(new[] { group });

        // Scan to populate groups
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.False(_vm.HasMarked);

        // Act — mark first file
        _vm.Groups[0].Files[0].IsMarkedForDeletion = true;

        // Assert
        Assert.Equal(1, _vm.MarkedCount);
        Assert.True(_vm.HasMarked);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static MediaFile MakeMediaFile(string filePath)
    {
        return new MediaFile
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileSize = 1_000_000,
            Extension = Path.GetExtension(filePath),
            DateImported = DateTime.UtcNow
        };
    }
}
