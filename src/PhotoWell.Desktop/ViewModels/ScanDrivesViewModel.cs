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
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

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

    [ObservableProperty]
    private int _importedCount;

    [ObservableProperty]
    private bool _isExcluded;

    public int  NewCount           => FileCount - ImportedCount;
    public bool IsFullyImported    => FileCount > 0 && NewCount == 0;
    public bool IsPartiallyImported => ImportedCount > 0 && !IsFullyImported && !IsExcluded;

    /// <summary>Human-readable import status shown in the results row.</summary>
    public string ImportStatus => IsExcluded        ? "Excluded" :
                                  IsFullyImported   ? "All imported" :
                                  ImportedCount > 0 ? $"{NewCount} new, {ImportedCount} already imported" :
                                                      "";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeLabel))]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeLabel))]
    private bool _isFullPath;

    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private string _editValue = "";

    public string TypeLabel => IsFullPath ? "Full Path" : "Folder Name";

    public ExclusionEntry(int id, string value, bool isFullPath)
    {
        Id         = id;
        _value     = value;
        _isFullPath = isFullPath;
    }

    public ExclusionEntry(ExclusionRule rule) : this(rule.Id, rule.Value, rule.IsFullPath) { }

    public void BeginEdit()  { EditValue = Value; IsEditing = true; }
    public void CancelEdit() { IsEditing = false; }
}

// ═══════════════════════════════════════════════════════════════════════
// Main ViewModel
// ═══════════════════════════════════════════════════════════════════════

public partial class ScanDrivesViewModel : ObservableObject
{
    private readonly IDriveService _driveService;
    private readonly IExclusionRepository _exclusionRepo;
    private readonly IMediaFileRepository _mediaRepo;
    private CancellationTokenSource? _cts;

    /// <summary>Populated when the user clicks Import. MainViewModel reads this after ShowDialog returns.</summary>
    public IReadOnlyList<string> SelectedFolderPaths { get; private set; } = [];

    /// <summary>Raised when the user confirms import — ScanDrivesWindow subscribes to close itself.</summary>
    public event Action? CloseRequested;

    // ── Collections ─────────────────────────────────────────────────────

    public ObservableCollection<SelectableDrive> Drives { get; } = new();
    public ObservableCollection<ExclusionEntry> ExcludedFolders { get; } = new();
    public ObservableCollection<SelectableFolder> ResultFolders { get; } = new();

    public ICollectionView ResultFoldersView { get; }

    public bool HasNoDrives      => Drives.Count == 0;
    public bool HasNoExclusions  => ExcludedFolders.Count == 0;

    // ── Observable Properties ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private bool _isScanning;

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
    private int _totalRaw;

    [ObservableProperty]
    private string _scanDuration = "";

    [ObservableProperty]
    private string _totalSizeDisplay = "";

    [ObservableProperty]
    private int _selectedTabIndex;

    private ScanResult? _lastScanResult;

    // ── Constructor ─────────────────────────────────────────────────────

    public ScanDrivesViewModel(
        IDriveService driveService,
        IExclusionRepository exclusionRepo,
        IMediaFileRepository mediaRepo)
    {
        _driveService  = driveService;
        _exclusionRepo = exclusionRepo;
        _mediaRepo     = mediaRepo;

        ResultFoldersView = CollectionViewSource.GetDefaultView(ResultFolders);

        Drives.CollectionChanged        += (_, _) => OnPropertyChanged(nameof(HasNoDrives));
        ExcludedFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoExclusions));

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
    private void BeginEditExclusion(ExclusionEntry entry) => entry.BeginEdit();

    [RelayCommand]
    private void CancelEditExclusion(ExclusionEntry entry) => entry.CancelEdit();

    [RelayCommand]
    private async Task SaveExclusionAsync(ExclusionEntry entry)
    {
        var newValue = entry.EditValue.Trim();
        if (string.IsNullOrEmpty(newValue)) { entry.CancelEdit(); return; }

        bool newIsFullPath = newValue.Contains(Path.DirectorySeparatorChar)
                          || newValue.Contains(Path.AltDirectorySeparatorChar)
                          || (newValue.Length >= 2 && newValue[1] == ':');

        await _exclusionRepo.UpdateAsync(entry.Id, newValue, newIsFullPath);
        entry.Value      = newValue;
        entry.IsFullPath = newIsFullPath;
        entry.IsEditing  = false;
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
                    ct: _cts.Token,
                    excludedPaths: pathExclusions);

                allFiles.AddRange(result.Files);
                totalDuration += result.Duration;
            }

            _lastScanResult = new ScanResult(
                allFiles.Count,
                allFiles.Count(f => f.Category == MediaCategory.Photo),
                allFiles.Count(f => f.Category == MediaCategory.Raw),
                allFiles.Sum(f => f.SizeBytes),
                allFiles,
                totalDuration);

            TotalPhotos = _lastScanResult.PhotoCount;
            TotalRaw = _lastScanResult.RawCount;
            FilesFound = _lastScanResult.TotalFiles;
            ScanDuration = $"{totalDuration.TotalSeconds:F1}s";
            TotalSizeDisplay = FormatSize(_lastScanResult.TotalSizeBytes);

            // Check which files are already in the library so we can dim/flag those folders.
            var importedPaths = await _mediaRepo.GetAllFilePathsAsync();

            foreach (var group in allFiles.GroupBy(f => f.Directory))
            {
                var files        = group.ToList();
                var importedCount = files.Count(f => importedPaths.Contains(f.FullPath));
                var folder = new SelectableFolder(group.Key)
                {
                    FileCount     = files.Count,
                    TotalSize     = files.Sum(f => f.SizeBytes),
                    ImportedCount = importedCount,
                    // Pre-uncheck fully-imported folders — user can still re-check to force re-import.
                    IsSelected    = importedCount < files.Count,
                };
                ResultFolders.Add(folder);
            }

            // Default sort: alphabetical by folder path
            _currentSortColumn = nameof(SelectableFolder.FolderPath);
            _currentSortDirection = ListSortDirection.Ascending;
            ApplySort();
            UpdateSortIndicators();

            StatusText = $"Scan complete — {allFiles.Count:N0} files in {ResultFolders.Count:N0} folders";
            ScanComplete = true;
            SelectedTabIndex = 2;   // auto-switch to Results tab
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
        foreach (var folder in ResultFolders.Where(f => !f.IsExcluded))
            folder.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllFolders()
    {
        foreach (var folder in ResultFolders.Where(f => !f.IsExcluded))
            folder.IsSelected = false;
    }

    [RelayCommand]
    private async Task IncludeFolderAsync(SelectableFolder folder)
    {
        if (folder is null) return;

        var path = folder.FolderPath;

        // Remove the exact-path exclusion rule if present.
        var matching = ExcludedFolders.FirstOrDefault(e => e.IsFullPath &&
                           e.Value.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (matching != null)
        {
            await _exclusionRepo.RemoveAsync(matching.Id);
            ExcludedFolders.Remove(matching);
        }

        folder.IsExcluded = false;
        folder.IsSelected = true;
        UpdateResultCounts();
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
            SelectedTabIndex = 1;   // switch to Exclusions tab so the user can see (and edit) the new entry
        }

        folder.IsExcluded  = true;
        folder.IsSelected  = false;
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
            SelectedTabIndex = 1;
        }

        foreach (var f in ResultFolders.Where(f => Path.GetFileName(f.FolderPath)
                     .Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            f.IsExcluded = true;
            f.IsSelected = false;
        }

        UpdateResultCounts();
    }

    private void UpdateResultCounts()
    {
        var active   = ResultFolders.Where(f => !f.IsExcluded).ToList();
        FilesFound       = active.Sum(f => f.FileCount);
        TotalSizeDisplay = FormatSize(active.Sum(f => f.TotalSize));
        StatusText       = $"Scan complete — {FilesFound:N0} files in {active.Count:N0} folders";
    }

    [RelayCommand]
    private void ImportSelected()
    {
        var paths = ResultFolders
            .Where(f => f.IsSelected && !f.IsExcluded)
            .Select(f => f.FolderPath)
            .ToList();

        if (paths.Count == 0)
        {
            StatusText = "No folders selected for import";
            return;
        }

        SelectedFolderPaths = paths;
        CloseRequested?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

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