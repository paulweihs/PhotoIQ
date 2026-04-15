using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Common;
using static PhotoIQPro.Common.FormatUtilities;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

// ── Top-level ─────────────────────────────────────────────────────────────────

public partial class FindDuplicatesViewModel : ObservableObject
{
    private readonly IMediaFileRepository _repo;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Scanning for duplicates…";
    [ObservableProperty] private ObservableCollection<DuplicateGroupViewModel> _groups = [];

    public int MarkedCount => Groups.Sum(g => g.Files.Count(f => f.IsMarkedForDeletion));
    public long MarkedBytes => Groups.Sum(g => g.Files.Where(f => f.IsMarkedForDeletion).Sum(f => f.File.FileSize));
    public string MarkedSizeText => FormatFileSize(MarkedBytes);
    public bool HasMarked => MarkedCount > 0;
    private bool CanScan => !IsScanning;

    public FindDuplicatesViewModel(IMediaFileRepository repo) => _repo = repo;

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        IsScanning = true;
        StatusText = "Scanning…";
        foreach (var g in Groups) g.SelectionChanged -= RefreshCounts;
        Groups.Clear();

        var duplicates = await _repo.GetDuplicateGroupsAsync();
        foreach (var group in duplicates)
        {
            var vm = new DuplicateGroupViewModel(group);
            vm.SelectionChanged += RefreshCounts;
            Groups.Add(vm);
        }

        StatusText = Groups.Count == 0
            ? "No duplicates found."
            : $"Found {Groups.Count} group{(Groups.Count == 1 ? "" : "s")} — select files to delete.";
        IsScanning = false;
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(MarkedBytes));
        OnPropertyChanged(nameof(MarkedSizeText));
        OnPropertyChanged(nameof(HasMarked));
    }

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private async Task DeleteMarkedAsync()
    {
        var toDelete = Groups
            .SelectMany(g => g.Files)
            .Where(f => f.IsMarkedForDeletion)
            .ToList();

        if (toDelete.Count == 0) return;

        var sizeText = FormatFileSize(toDelete.Sum(f => f.File.FileSize));
        var names = string.Join("\n", toDelete.Take(8).Select(f => $"  • {f.FileName}"));
        if (toDelete.Count > 8) names += $"\n  … and {toDelete.Count - 8} more";

        var msg = $"Permanently delete {toDelete.Count} file{(toDelete.Count == 1 ? "" : "s")} ({sizeText}) from disk?\n\n{names}\n\nThis cannot be undone.";
        var result = System.Windows.MessageBox.Show(
            msg, "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        int deleted = 0;
        var failedNames = new List<string>();
        foreach (var item in toDelete)
        {
            try
            {
                // Remove from library first — a dangling DB record is recoverable;
                // a file deleted from disk with the record intact is not.
                await _repo.DeleteAsync(item.File.Id);
                if (File.Exists(item.File.FilePath))
                    File.Delete(item.File.FilePath);
                deleted++;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Failed to delete duplicate '{item.File.FilePath}': {ex.GetType().Name}: {ex.Message}");
                failedNames.Add(item.FileName);
            }
        }

        if (failedNames.Count > 0)
        {
            var sample = string.Join(", ", failedNames.Take(3));
            var more   = failedNames.Count > 3 ? $" and {failedNames.Count - 3} more" : "";
            StatusText = $"Deleted {deleted} file{(deleted == 1 ? "" : "s")} — {failedNames.Count} could not be removed ({sample}{more}). They may be open in another application.";
        }
        else
        {
            StatusText = $"Deleted {deleted} file{(deleted == 1 ? "" : "s")}.";
        }
        await ScanAsync();
    }

}

// ── Group ─────────────────────────────────────────────────────────────────────

public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupType Type { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public event Action? SelectionChanged;

    public bool IsExactHash   => Type == DuplicateGroupType.ExactHash;
    public bool IsRawJpegPair => Type == DuplicateGroupType.RawJpegPair;

    public string TypeLabel => Type switch
    {
        DuplicateGroupType.ExactHash    => "EXACT COPY",
        DuplicateGroupType.RawJpegPair  => "RAW + JPG PAIR",
        _                               => "DUPLICATE"
    };

    public string TypeHint => Type switch
    {
        DuplicateGroupType.RawJpegPair =>
            "Same shot in two formats. Keep RAW for editing, or JPG to save space.",
        _ => "Identical files — safe to keep one and delete the rest."
    };

    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Type = group.Type;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(f => new DuplicateFileViewModel(f, this)));
    }

    internal void NotifySelectionChanged() => SelectionChanged?.Invoke();

    /// <summary>Mark all non-RAW files for deletion, keeping RAW originals.</summary>
    [RelayCommand]
    private void KeepRaw()
    {
        foreach (var f in Files)
            f.IsMarkedForDeletion = !IsRawExtension(f.ExtBadge);
    }

    /// <summary>Mark all RAW files for deletion, keeping the JPG.</summary>
    [RelayCommand]
    private void KeepJpg()
    {
        foreach (var f in Files)
            f.IsMarkedForDeletion = !IsJpegExtension(f.ExtBadge);
    }

    /// <summary>Mark all but the most recently dated file for deletion.</summary>
    [RelayCommand]
    private void KeepNewest()
    {
        var newest = Files.MaxBy(f => f.File.DateTaken ?? f.File.DateImported);
        foreach (var f in Files)
            f.IsMarkedForDeletion = f != newest;
    }

    /// <summary>Mark all but the oldest dated file for deletion.</summary>
    [RelayCommand]
    private void KeepOldest()
    {
        var oldest = Files.MinBy(f => f.File.DateTaken ?? f.File.DateImported);
        foreach (var f in Files)
            f.IsMarkedForDeletion = f != oldest;
    }

    /// <summary>Clear all deletion marks in this group.</summary>
    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var f in Files)
            f.IsMarkedForDeletion = false;
    }

    private static bool IsRawExtension(string ext) =>
        ext is "ARW" or "CR2" or "CR3" or "NEF" or "ORF" or "RAF" or "RW2"
            or "DNG" or "PEF" or "SRW" or "NRW" or "3FR" or "MOS" or "IIQ";

    private static bool IsJpegExtension(string ext) =>
        ext is "JPG" or "JPEG";
}

// ── File item ─────────────────────────────────────────────────────────────────

public partial class DuplicateFileViewModel : ObservableObject
{
    private readonly DuplicateGroupViewModel _group;

    public MediaFile File { get; }

    [ObservableProperty] private bool _isMarkedForDeletion;

    public string FileName   => File.FileName;
    public string FolderPath => Path.GetDirectoryName(File.FilePath) ?? "";
    public string SizeText   => FormatFileSize(File.FileSize);
    public string DateText   => File.DateTaken?.ToString("yyyy-MM-dd") ?? File.DateImported.ToString("yyyy-MM-dd");
    public string ExtBadge   => File.Extension.TrimStart('.').ToUpperInvariant();

    public DuplicateFileViewModel(MediaFile file, DuplicateGroupViewModel group)
    {
        File = file;
        _group = group;
    }

    partial void OnIsMarkedForDeletionChanged(bool value) => _group.NotifySelectionChanged();

}
