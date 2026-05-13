using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using static PhotoWell.Common.FormatUtilities;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

// ── Top-level ─────────────────────────────────────────────────────────────────

public partial class FindDuplicatesViewModel : ObservableObject
{
    private readonly IMediaFileRepository _repo;
    private readonly IServiceScopeFactory _scopeFactory;

    // Persisted set of folders the user has asked to ignore during duplicate checks.
    // Stored case-insensitively; each entry is a directory path with no trailing separator.
    private readonly HashSet<string> _ignoredFolders;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Scanning for duplicates…";
    [ObservableProperty] private ObservableCollection<DuplicateGroupViewModel> _groups = [];

    public int MarkedCount => Groups.Sum(g => g.Files.Count(f => f.IsMarkedForDeletion));
    public long MarkedBytes => Groups.Sum(g => g.Files.Where(f => f.IsMarkedForDeletion).Sum(f => f.File.FileSize));
    public string MarkedSizeText => FormatFileSize(MarkedBytes);
    public bool HasMarked => MarkedCount > 0;
    public bool HasNoResults => !IsScanning && Groups.Count == 0;
    public int IgnoredFolderCount => _ignoredFolders.Count;
    private bool CanScan => !IsScanning;

    public FindDuplicatesViewModel(IMediaFileRepository repo, IServiceScopeFactory scopeFactory)
    {
        _repo = repo;
        _scopeFactory = scopeFactory;
        _ignoredFolders = LoadIgnoredFolders();
        Groups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoResults));
    }

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        IsScanning = true;
        StatusText = "Scanning…";
        ClearAndUnsubscribe();

        var progress = new Progress<string>(msg => StatusText = msg);
        var allDuplicates = (await _repo.GetDuplicateGroupsAsync(progress)).ToList();

        var visible = allDuplicates.Where(g => !IsGroupIgnored(g)).ToList();
        int hiddenCount = allDuplicates.Count - visible.Count;

        PopulateGroupsWithListeners(visible);

        StatusText = BuildScanResultMessage(Groups.Count, hiddenCount);
        IsScanning = false;
        RefreshCounts();
    }

    // ── Context-menu commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void OpenFileInExplorer(DuplicateFileViewModel? fileVm)
    {
        if (fileVm?.File.FilePath is not string path) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyFilePath(DuplicateFileViewModel? fileVm)
    {
        if (fileVm?.File.FilePath is string path)
            Clipboard.SetText(path);
    }

    [RelayCommand]
    private async Task ExcludePhoto(DuplicateFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        await _repo.ExcludeAsync(fileVm.File.Id);
        RemoveFileFromGroups(fileVm);
        StatusText = $"'{fileVm.FileName}' excluded from library.";
    }

    [RelayCommand]
    private async Task ExcludeFolder(DuplicateFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        var folderPath = Path.GetDirectoryName(fileVm.File.FilePath);
        if (string.IsNullOrEmpty(folderPath)) return;

        var owner = Application.Current.Windows.OfType<Views.FindDuplicatesWindow>().FirstOrDefault()
                    ?? Application.Current.MainWindow;
        var dialog = new Views.ExcludeFolderDialog(folderPath, owner);
        if (dialog.ShowDialog() != true) return;

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var exclusionRepo = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
                var mediaRepo     = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                await exclusionRepo.AddAsync(new ExclusionRule { Value = folderPath, IsFullPath = true });
                await mediaRepo.RemoveByFolderAsync(folderPath, dialog.IncludeSubfolders);
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not exclude folder: {ex.Message}";
            return;
        }

        var normalizedFolder = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var toRemove = Groups.SelectMany(g => g.Files)
            .Where(f =>
            {
                var dir = (Path.GetDirectoryName(f.File.FilePath) ?? "")
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return dialog.IncludeSubfolders
                    ? dir.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
                      dir.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    : dir.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase);
            }).ToList();

        foreach (var f in toRemove)
            RemoveFileFromGroups(f);

        StatusText = $"Folder excluded — {toRemove.Count} photo(s) removed.";
    }

    [RelayCommand]
    private void IgnoreFolderForDuplicateCheck(DuplicateFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        var folderPath = Path.GetDirectoryName(fileVm.File.FilePath);
        if (string.IsNullOrEmpty(folderPath)) return;

        var normalized = NormalizeFolder(folderPath);
        if (!_ignoredFolders.Add(normalized)) return; // already ignored

        SaveIgnoredFolders();
        OnPropertyChanged(nameof(IgnoredFolderCount));

        // Remove currently-displayed groups that are now fully covered by ignored folders.
        foreach (var group in Groups.Where(g => IsGroupViewModelIgnored(g)).ToList())
        {
            group.SelectionChanged -= RefreshCounts;
            Groups.Remove(group);
        }
        RefreshCounts();

        StatusText = $"Folder will be skipped in future scans. "
                   + $"{_ignoredFolders.Count} folder(s) ignored for duplicate check.";
    }

    [RelayCommand]
    public async Task ClearIgnoredFolders()
    {
        _ignoredFolders.Clear();
        SaveIgnoredFolders();
        OnPropertyChanged(nameof(IgnoredFolderCount));
        await ScanAsync();
    }

    // ── Ignored-folder persistence ────────────────────────────────────────────

    private static HashSet<string> LoadIgnoredFolders()
    {
        try
        {
            if (!File.Exists(AppSettings.DuplicateIgnoredFoldersPath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(
                File.ReadAllLines(AppSettings.DuplicateIgnoredFoldersPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveIgnoredFolders()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.DuplicateIgnoredFoldersPath)!);
            File.WriteAllLines(AppSettings.DuplicateIgnoredFoldersPath, _ignoredFolders);
        }
        catch { /* best-effort */ }
    }

    private static string NormalizeFolder(string folderPath) =>
        folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // True when every file in the group (model) lives in an ignored folder.
    private bool IsGroupIgnored(DuplicateGroup group) =>
        group.Files.All(f => _ignoredFolders.Contains(NormalizeFolder(
            Path.GetDirectoryName(f.FilePath) ?? "")));

    // Same check on the live view-model (used after scan when models are already wrapped).
    private bool IsGroupViewModelIgnored(DuplicateGroupViewModel group) =>
        group.Files.All(f => _ignoredFolders.Contains(NormalizeFolder(
            Path.GetDirectoryName(f.File.FilePath) ?? "")));

    private void RemoveFileFromGroups(DuplicateFileViewModel fileVm)
    {
        foreach (var group in Groups.ToList())
        {
            if (!group.Files.Contains(fileVm)) continue;
            group.Files.Remove(fileVm);
            if (group.Files.Count <= 1)
            {
                group.SelectionChanged -= RefreshCounts;
                Groups.Remove(group);
            }
        }
        RefreshCounts();
    }

    private void ClearAndUnsubscribe()
    {
        foreach (var g in Groups)
            g.SelectionChanged -= RefreshCounts;
        Groups.Clear();
    }

    private void PopulateGroupsWithListeners(IEnumerable<DuplicateGroup> duplicates)
    {
        foreach (var group in duplicates)
        {
            var vm = new DuplicateGroupViewModel(group);
            vm.SelectionChanged += RefreshCounts;
            Groups.Add(vm);
        }
    }

    private string BuildScanResultMessage(int groupCount, int hiddenCount = 0)
    {
        var main = groupCount == 0
            ? "No duplicates found."
            : $"Found {groupCount} group{(groupCount == 1 ? "" : "s")} — select files to delete.";
        if (hiddenCount > 0)
            main += $" ({hiddenCount} hidden — ignored folder{(hiddenCount == 1 ? "" : "s")})";
        return main;
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(MarkedBytes));
        OnPropertyChanged(nameof(MarkedSizeText));
        OnPropertyChanged(nameof(HasMarked));
    }

    private string BuildDeleteConfirmMessage(List<DuplicateFileViewModel> toDelete)
    {
        var sizeText = FormatFileSize(toDelete.Sum(f => f.File.FileSize));
        var names = string.Join("\n", toDelete.Take(8).Select(f => $"  • {f.FileName}"));
        if (toDelete.Count > 8) names += $"\n  … and {toDelete.Count - 8} more";
        return $"Permanently delete {toDelete.Count} file{(toDelete.Count == 1 ? "" : "s")} ({sizeText}) from disk?\n\n{names}\n\nThis cannot be undone.";
    }

    private async Task<(int Deleted, List<string> FailedNames)> DeleteFilesAsync(
        List<DuplicateFileViewModel> toDelete)
    {
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
        return (deleted, failedNames);
    }

    private string BuildDeleteResultMessage(int deleted, List<string> failedNames)
    {
        if (failedNames.Count > 0)
        {
            var sample = string.Join(", ", failedNames.Take(3));
            var more   = failedNames.Count > 3 ? $" and {failedNames.Count - 3} more" : "";
            return $"Deleted {deleted} file{(deleted == 1 ? "" : "s")} — {failedNames.Count} could not be removed ({sample}{more}). They may be open in another application.";
        }
        return $"Deleted {deleted} file{(deleted == 1 ? "" : "s")}.";
    }

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private async Task DeleteMarkedAsync()
    {
        var toDelete = Groups
            .SelectMany(g => g.Files)
            .Where(f => f.IsMarkedForDeletion)
            .ToList();

        if (toDelete.Count == 0) return;

        var result = System.Windows.MessageBox.Show(
            BuildDeleteConfirmMessage(toDelete), "Confirm Delete",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        var (deleted, failedNames) = await DeleteFilesAsync(toDelete);
        StatusText = BuildDeleteResultMessage(deleted, failedNames);
        await ScanAsync();
    }

}

// ── Group ─────────────────────────────────────────────────────────────────────

public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupType Type { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public event Action? SelectionChanged;

    public bool IsExactHash        => Type == DuplicateGroupType.ExactHash;
    public bool IsRawJpegPair      => Type == DuplicateGroupType.RawJpegPair;
    public bool IsPerceptualSimilar => Type == DuplicateGroupType.PerceptualSimilar;

    public string TypeLabel => Type switch
    {
        DuplicateGroupType.ExactHash         => "EXACT COPY",
        DuplicateGroupType.RawJpegPair       => "RAW + JPG PAIR",
        DuplicateGroupType.PerceptualSimilar => "NEAR DUPLICATE",
        _                                    => "DUPLICATE"
    };

    public string TypeHint => Type switch
    {
        DuplicateGroupType.RawJpegPair =>
            "Same shot in two formats. Keep RAW for editing, or JPG to save space.",
        DuplicateGroupType.PerceptualSimilar =>
            "Visually near-identical photos (similar crop, exposure, or burst shot). Review before deleting.",
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

    private static readonly HashSet<string> _rawBadges = new(StringComparer.OrdinalIgnoreCase)
        { "ARW", "CR2", "CR3", "NEF", "ORF", "RAF", "RW2", "DNG", "PEF", "SRW", "NRW", "3FR", "MOS", "IIQ" };
    private static readonly HashSet<string> _jpgBadges = new(StringComparer.OrdinalIgnoreCase)
        { "JPG", "JPEG" };

    public bool IsRawBadge => _rawBadges.Contains(ExtBadge);
    public bool IsJpgBadge => _jpgBadges.Contains(ExtBadge);

    public DuplicateFileViewModel(MediaFile file, DuplicateGroupViewModel group)
    {
        File = file;
        _group = group;
    }

    partial void OnIsMarkedForDeletionChanged(bool value) => _group.NotifySelectionChanged();

}
