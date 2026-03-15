using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

// ═══════════════════════════════════════════════════════════════════════
// Selectable item wrappers
// ═══════════════════════════════════════════════════════════════════════

public partial class SelectableDrive : ObservableObject
{
    public DriveInfoDto Drive { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string DisplayName => string.IsNullOrWhiteSpace(Drive.Label)
        ? $"{Drive.Name} ({Drive.DriveType})"
        : $"{Drive.Name} {Drive.Label} ({Drive.DriveType})";

    public string SizeInfo => $"{FormatSize(Drive.FreeSpace)} free of {FormatSize(Drive.TotalSize)}";

    public SelectableDrive(DriveInfoDto drive) => Drive = drive;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB",
        >= 1L << 30 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1L << 20 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };
}

public partial class SelectableFolder : ObservableObject
{
    public string FolderPath { get; }
    public string FolderName => Path.GetFileName(FolderPath) ?? FolderPath;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private long _totalSize;

    public string SizeDisplay => TotalSize switch
    {
        >= 1L << 30 => $"{TotalSize / (1024.0 * 1024 * 1024):F1} GB",
        >= 1L << 20 => $"{TotalSize / (1024.0 * 1024):F1} MB",
        _ => $"{TotalSize / 1024.0:F0} KB"
    };

    public SelectableFolder(string path) => FolderPath = path;
}

// ═══════════════════════════════════════════════════════════════════════
// Exclusion display wrapper (wraps the EF entity for UI binding)
// ═══════════════════════════════════════════════════════════════════════

public partial class ExclusionEntry : ObservableObject
{
    public int Id { get; }
    public string Value { get; }
    public bool IsFullPath { get; }
    public string DisplayText => IsFullPath ? $"📁 {Value}" : $"📛 {Value}";
    public string TypeLabel => IsFullPath ? "Full Path" : "Folder Name";

    public ExclusionEntry(int id, string value, bool isFullPath)
    {
        Id = id;
        Value = value;
        IsFullPath = isFullPath;
    }

    public ExclusionEntry(ExclusionRule rule)
        : this(rule.Id, rule.Value, rule.IsFullPath) { }
}

// ═══════════════════════════════════════════════════════════════════════
// Main ViewModel
// ═══════════════════════════════════════════════════════════════════════

public partial class ScanDrivesViewModel : ObservableObject
{
    private readonly IDriveService _driveService;
    private readonly IImportService _importService;
    private readonly IExclusionRepository _exclusionRepo;
    private CancellationTokenSource? _cts;

    // ── Collections ─────────────────────────────────────────────────────

    public ObservableCollection<SelectableDrive> Drives { get; } = new();
    public ObservableCollection<ExclusionEntry> ExcludedFolders { get; } = new();
    public ObservableCollection<SelectableFolder> ResultFolders { get; } = new();

    public ICollectionView ResultFoldersView { get; }

    // ── Observable Properties ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private bool _scanComplete;

    [ObservableProperty]
    private string _statusText = "Select drives to scan";

    [ObservableProperty]
    private string _progressDetail = "";

    [ObservableProperty]
    private int _foldersScanned;

    [ObservableProperty]
    private int _filesFound;

    [ObservableProperty]
    private string _newExclusion = "";

    // ── Current sort state ──────────────────────────────────────────────

    private string _currentSortColumn = nameof(SelectableFolder.FolderPath);
    private ListSortDirection _currentSortDirection = ListSortDirection.Ascending;

    [ObservableProperty]
    private string _folderPathSortIndicator = " ▲";

    [ObservableProperty]
    private string _fileCountSortIndicator = "";

    [ObservableProperty]
    private string _sizeSortIndicator = "";

    // ── Scan Result Summary ─────────────────────────────────────────────

    [ObservableProperty]
    private int _totalPhotos;

    [ObservableProperty]
    private int _totalVideos;

    [ObservableProperty]
    private int _totalRaw;

    [ObservableProperty]
    private string _scanDuration = "";

    [ObservableProperty]
    private string _totalSizeDisplay = "";

    // ── Import tracking ─────────────────────────────────────────────────

    [ObservableProperty]
    private int _importedCount;

    [ObservableProperty]
    private int _importTotal;

    private ScanResult? _lastScanResult;

    // ── Constructor ─────────────────────────────────────────────────────

    public ScanDrivesViewModel(
        IDriveService driveService,
        IImportService importService,
        IExclusionRepository exclusionRepo)
    {
        _driveService = driveService;
        _importService = importService;
        _exclusionRepo = exclusionRepo;

        ResultFoldersView = CollectionViewSource.GetDefaultView(ResultFolders);

        LoadDrives();

        // Fire-and-forget load from DB (UI thread safe via ObservableCollection)
        _ = LoadExclusionsAsync();
    }

    // ── Initialization ──────────────────────────────────────────────────

    private void LoadDrives()
    {
        Drives.Clear();
        foreach (var drive in _driveService.GetAvailableDrives())
            Drives.Add(new SelectableDrive(drive));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Exclusion Persistence (Database)
    // ═══════════════════════════════════════════════════════════════════

    private async Task LoadExclusionsAsync()
    {
        ExcludedFolders.Clear();

        var rules = await _exclusionRepo.GetAllAsync();

        if (rules.Count == 0)
        {
            // First run — seed defaults
            await SeedDefaultExclusionsAsync();
            return;
        }

        foreach (var rule in rules)
            ExcludedFolders.Add(new ExclusionEntry(rule));
    }

    private async Task SeedDefaultExclusionsAsync()
    {
        var defaults = _driveService.DefaultExclusions
            .Select(name => new ExclusionRule { Value = name, IsFullPath = false })
            .ToList();

        await _exclusionRepo.ReplaceAllAsync(defaults);

        // Reload to get IDs
        var rules = await _exclusionRepo.GetAllAsync();
        ExcludedFolders.Clear();
        foreach (var rule in rules)
            ExcludedFolders.Add(new ExclusionEntry(rule));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Exclusion Commands
    // ═══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task AddExclusionAsync()
    {
        var trimmed = NewExclusion.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        bool isFullPath = trimmed.Contains(Path.DirectorySeparatorChar)
                       || trimmed.Contains(Path.AltDirectorySeparatorChar)
                       || (trimmed.Length >= 2 && trimmed[1] == ':');

        if (ExcludedFolders.Any(e => e.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                                  && e.IsFullPath == isFullPath))
            return;

        var rule = new ExclusionRule { Value = trimmed, IsFullPath = isFullPath };
        await _exclusionRepo.AddAsync(rule);

        ExcludedFolders.Add(new ExclusionEntry(rule));
        NewExclusion = "";
    }

    [RelayCommand]
    private async Task BrowseForExclusionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to exclude from scanning"
        };

        bool? result = dialog.ShowDialog();
        if (result is not true) return;

        var path = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (ExcludedFolders.Any(e => e.Value.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(
                $"\"{path}\" is already in the exclusion list.",
                "Already Excluded",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var rule = new ExclusionRule { Value = path, IsFullPath = true };
        await _exclusionRepo.AddAsync(rule);

        ExcludedFolders.Add(new ExclusionEntry(rule));

        System.Windows.MessageBox.Show(
            $"\"{path}\" was added to the exclude list.",
            "Folder Excluded",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync(ExclusionEntry entry)
    {
        await _exclusionRepo.RemoveAsync(entry.Id);
        ExcludedFolders.Remove(entry);
    }

    [RelayCommand]
    private async Task ResetExclusionsAsync()
    {
        await SeedDefaultExclusionsAsync();
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        LoadDrives();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Column Sorting
    // ═══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void SortByColumn(string columnName)
    {
        if (_currentSortColumn == columnName)
        {
            _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _currentSortColumn = columnName;
            _currentSortDirection = columnName == nameof(SelectableFolder.FolderPath)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
        }

        ApplySort();
        UpdateSortIndicators();
    }

    private void ApplySort()
    {
        ResultFoldersView.SortDescriptions.Clear();
        ResultFoldersView.SortDescriptions.Add(
            new SortDescription(_currentSortColumn, _currentSortDirection));
    }

    private void UpdateSortIndicators()
    {
        var arrow = _currentSortDirection == ListSortDirection.Ascending ? " ▲" : " ▼";

        FolderPathSortIndicator = _currentSortColumn == nameof(SelectableFolder.FolderPath) ? arrow : "";
        FileCountSortIndicator = _currentSortColumn == nameof(SelectableFolder.FileCount) ? arrow : "";
        SizeSortIndicator = _currentSortColumn == nameof(SelectableFolder.TotalSize) ? arrow : "";
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scan
    // ═══════════════════════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        var selectedDrives = Drives.Where(d => d.IsSelected).ToList();
        if (selectedDrives.Count == 0)
        {
            StatusText = "Please select at least one drive";
            return;
        }

        IsScanning = true;
        ScanComplete = false;
        ResultFolders.Clear();
        FoldersScanned = 0;
        FilesFound = 0;
        _lastScanResult = null;

        _cts = new CancellationTokenSource();

        try
        {
            var allFiles = new System.Collections.Generic.List<FoundMediaFile>();
            var totalDuration = TimeSpan.Zero;

            var nameExclusions = ExcludedFolders
                .Where(e => !e.IsFullPath)
                .Select(e => e.Value)
                .ToList()
                .AsReadOnly();

            var pathExclusions = ExcludedFolders
                .Where(e => e.IsFullPath)
                .Select(e => e.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in selectedDrives)
            {
                StatusText = $"Scanning {drive.Drive.Name}...";

                var progress = new Progress<ScanProgress>(p =>
                {
                    FoldersScanned = p.FoldersScanned;
                    ProgressDetail = ShortenPath(p.CurrentFolder, 70);
                });

                var result = await _driveService.ScanForMediaAsync(
                    drive.Drive.Name,
                    recursive: true,
                    excludedFolders: nameExclusions,
                    progress: progress,
                    ct: _cts.Token);

                var filtered = result.Files
                    .Where(f => !pathExclusions.Any(ex =>
                        f.Directory.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                        f.Directory.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                allFiles.AddRange(filtered);
                totalDuration += result.Duration;
            }

            _lastScanResult = new ScanResult(
                allFiles.Count,
                allFiles.Count(f => f.Category == MediaCategory.Photo),
                allFiles.Count(f => f.Category == MediaCategory.Video),
                allFiles.Count(f => f.Category == MediaCategory.Raw),
                allFiles.Sum(f => f.SizeBytes),
                allFiles,
                totalDuration);

            TotalPhotos = _lastScanResult.PhotoCount;
            TotalVideos = _lastScanResult.VideoCount;
            TotalRaw = _lastScanResult.RawCount;
            FilesFound = _lastScanResult.TotalFiles;
            ScanDuration = $"{totalDuration.TotalSeconds:F1}s";
            TotalSizeDisplay = FormatSize(_lastScanResult.TotalSizeBytes);

            foreach (var group in allFiles.GroupBy(f => f.Directory))
            {
                ResultFolders.Add(new SelectableFolder(group.Key)
                {
                    FileCount = group.Count(),
                    TotalSize = group.Sum(f => f.SizeBytes),
                    IsSelected = true
                });
            }

            // Default sort: alphabetical by folder path
            _currentSortColumn = nameof(SelectableFolder.FolderPath);
            _currentSortDirection = ListSortDirection.Ascending;
            ApplySort();
            UpdateSortIndicators();

            StatusText = $"Scan complete — {allFiles.Count:N0} files in {ResultFolders.Count:N0} folders";
            ScanComplete = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStartScan() => !IsScanning;

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    // ═══════════════════════════════════════════════════════════════════
    // Results Commands
    // ═══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void SelectAllFolders()
    {
        foreach (var folder in ResultFolders)
            folder.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllFolders()
    {
        foreach (var folder in ResultFolders)
            folder.IsSelected = false;
    }

    [RelayCommand]
    private async Task ExcludeFolderAsync(SelectableFolder folder)
    {
        if (folder is null) return;

        var path = folder.FolderPath;

        if (!ExcludedFolders.Any(e => e.Value.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            var rule = new ExclusionRule { Value = path, IsFullPath = true };
            await _exclusionRepo.AddAsync(rule);
            ExcludedFolders.Add(new ExclusionEntry(rule));
        }

        ResultFolders.Remove(folder);
        UpdateResultCounts();
    }

    [RelayCommand]
    private async Task ExcludeFolderByNameAsync(SelectableFolder folder)
    {
        if (folder is null) return;

        var name = folder.FolderName;

        if (!ExcludedFolders.Any(e => !e.IsFullPath && e.Value.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var rule = new ExclusionRule { Value = name, IsFullPath = false };
            await _exclusionRepo.AddAsync(rule);
            ExcludedFolders.Add(new ExclusionEntry(rule));
        }

        var toRemove = ResultFolders
            .Where(f => Path.GetFileName(f.FolderPath).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in toRemove)
            ResultFolders.Remove(item);

        UpdateResultCounts();
    }

    private void UpdateResultCounts()
    {
        FilesFound = ResultFolders.Sum(f => f.FileCount);
        TotalSizeDisplay = FormatSize(ResultFolders.Sum(f => f.TotalSize));
        StatusText = $"Scan complete — {FilesFound:N0} files in {ResultFolders.Count:N0} folders";
    }

    [RelayCommand]
    private async Task ImportSelectedAsync()
    {
        if (_lastScanResult is null) return;

        var selectedPaths = ResultFolders
            .Where(f => f.IsSelected)
            .Select(f => f.FolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedPaths.Count == 0)
        {
            StatusText = "No folders selected for import";
            return;
        }

        var filesToImport = _lastScanResult.Files
            .Where(f => selectedPaths.Contains(f.Directory))
            .ToList();

        IsImporting = true;
        ImportedCount = 0;
        ImportTotal = filesToImport.Count;
        StatusText = $"Importing {ImportTotal:N0} files...";

        try
        {
            foreach (var folderPath in selectedPaths)
            {
                var folderFiles = filesToImport.Where(f => f.Directory == folderPath).ToList();
                if (folderFiles.Count == 0) continue;

                ProgressDetail = ShortenPath(folderPath, 70);

                await _importService.ImportFolderAsync(folderPath, recursive: false, null, default);
                ImportedCount += folderFiles.Count;
                StatusText = $"Imported {ImportedCount:N0} of {ImportTotal:N0} files...";
            }

            StatusText = $"Import complete — {ImportedCount:N0} files imported";
        }
        catch (Exception ex)
        {
            StatusText = $"Import error: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    public System.Collections.Generic.List<string> GetSelectedFolderPaths()
    {
        return ResultFolders
            .Where(f => f.IsSelected)
            .Select(f => f.FolderPath)
            .ToList();
    }

    private static string ShortenPath(string path, int maxLength)
    {
        if (path.Length <= maxLength) return path;
        return "..." + path[^(maxLength - 3)..];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB",
        >= 1L << 30 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1L << 20 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };
}