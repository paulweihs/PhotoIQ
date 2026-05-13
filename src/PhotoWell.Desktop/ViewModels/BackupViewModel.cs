using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    // ── Context ──────────────────────────────────────────────────────────────

    /// <summary>Photo IDs pre-selected for export. Empty = no selection (export tab shows guidance).</summary>
    public IReadOnlyList<Guid> SelectedPhotoIds { get; private set; } = Array.Empty<Guid>();

    /// <summary>Whether the window was opened with a photo selection (drives which tab is active).</summary>
    public bool HasSelection => SelectedPhotoIds.Count > 0;

    public string SelectionSummary => SelectedPhotoIds.Count switch
    {
        0 => "No photos selected — select photos in the gallery first.",
        1 => "1 photo selected for export.",
        _ => $"{SelectedPhotoIds.Count} photos selected for export.",
    };

    // ── Backup tab ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _includeThumbnails;
    [ObservableProperty] private string _backupSizeEstimate = "Calculating…";
    [ObservableProperty] private bool   _isBackupRunning;
    [ObservableProperty] private string _backupStatus       = "";
    [ObservableProperty] private int    _backupProgress;      // 0–100
    [ObservableProperty] private bool   _backupDone;

    // ── Restore tab ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _restoreFilePath     = "";
    [ObservableProperty] private bool   _isRestoreRunning;
    [ObservableProperty] private string _restoreStatus       = "";
    [ObservableProperty] private int    _restoreProgress;
    [ObservableProperty] private bool   _restoreDone;

    // ── Export tab ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isExportRunning;
    [ObservableProperty] private string _exportStatus        = "";
    [ObservableProperty] private int    _exportProgress;      // 0–100
    [ObservableProperty] private bool   _exportDone;

    // ── Import bundle tab ────────────────────────────────────────────────────

    [ObservableProperty] private string _bundleFilePath      = "";
    [ObservableProperty] private string _bundleSummary       = "";
    public bool HasBundleSummary => BundleSummary.Length > 0;
    [ObservableProperty] private string _bundleDestFolder    = "";
    [ObservableProperty] private bool   _isBundleImportRunning;
    [ObservableProperty] private string _bundleImportStatus  = "";
    [ObservableProperty] private int    _bundleImportProgress;
    [ObservableProperty] private bool   _bundleImportDone;

    /// <summary>Raised when the restore completes so MainViewModel can reload the gallery.</summary>
    public event Action? RestoreCompleted;

    /// <summary>Raised when bundle import completes so the gallery can be refreshed.</summary>
    public event Action? BundleImportCompleted;

    public BackupViewModel(IServiceScopeFactory scopeFactory,
        IReadOnlyList<Guid>? selectedPhotoIds = null)
    {
        _scopeFactory    = scopeFactory;
        SelectedPhotoIds = selectedPhotoIds ?? Array.Empty<Guid>();
        _ = RefreshSizeEstimateAsync();
    }

    // ── Partial property callbacks ────────────────────────────────────────────

    partial void OnIncludeThumbnailsChanged(bool value) => _ = RefreshSizeEstimateAsync();

    private async Task RefreshSizeEstimateAsync()
    {
        BackupSizeEstimate = "Calculating…";
        using var scope = _scopeFactory.CreateScope();
        var svc  = scope.ServiceProvider.GetRequiredService<IExportService>();
        var size = await svc.EstimateBackupSizeAsync(IncludeThumbnails);
        BackupSizeEstimate = FormatBytes(size);
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunBackup))]
    private async Task BackupAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save Backup",
            Filter           = "PhotoWell Backup (*.zip)|*.zip",
            FileName         = $"photowell-backup-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt       = ".zip",
        };
        if (dlg.ShowDialog() != true) return;

        IsBackupRunning = true;
        BackupDone      = false;
        BackupStatus    = "Starting…";
        BackupProgress  = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExportService>();
            var progress = new Progress<ExportProgress>(p =>
            {
                BackupProgress = p.Total > 0 ? (int)((double)p.Processed / p.Total * 100) : 0;
                BackupStatus   = p.CurrentFile;
            });
            await svc.CreateBackupAsync(dlg.FileName, IncludeThumbnails, progress);
            BackupProgress = 100;
            BackupStatus   = $"Backup saved to {Path.GetFileName(dlg.FileName)}";
            BackupDone     = true;
        }
        catch (Exception ex)
        {
            BackupStatus = $"Error: {ex.Message}";
        }
        finally { IsBackupRunning = false; }
    }

    private bool CanRunBackup() => !IsBackupRunning && !IsRestoreRunning;

    // ── Restore ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseRestoreFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title     = "Select Backup ZIP",
            Filter    = "PhotoWell Backup (*.zip)|*.zip",
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true)
            RestoreFilePath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanRunRestore))]
    private async Task RestoreAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            "Restoring will replace your current library database with the backup.\n\n" +
            "Any photos, tags, or descriptions added since the backup was created will be lost.\n\n" +
            "Continue?",
            "Confirm Restore",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsRestoreRunning = true;
        RestoreDone      = false;
        RestoreStatus    = "Closing database connection…";
        RestoreProgress  = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExportService>();
            var progress = new Progress<ExportProgress>(p =>
            {
                RestoreProgress = p.Total > 0 ? (int)((double)p.Processed / p.Total * 100) : 50;
                RestoreStatus   = p.CurrentFile;
            });
            await svc.RestoreBackupAsync(RestoreFilePath, progress);
            RestoreProgress = 100;
            RestoreStatus   = "Restore complete. Reloading library…";
            RestoreDone     = true;
            RestoreCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            RestoreStatus = $"Error: {ex.Message}";
        }
        finally { IsRestoreRunning = false; }
    }

    private bool CanRunRestore() => !string.IsNullOrEmpty(RestoreFilePath)
                                 && File.Exists(RestoreFilePath)
                                 && !IsRestoreRunning
                                 && !IsBackupRunning;

    partial void OnRestoreFilePathChanged(string value) => RestoreCommand.NotifyCanExecuteChanged();

    // ── Export bundle ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private async Task ExportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save Export Bundle",
            Filter     = "PhotoWell Bundle (*.pwbundle)|*.pwbundle",
            FileName   = $"photowell-export-{DateTime.Now:yyyy-MM-dd}.pwbundle",
            DefaultExt = ".pwbundle",
        };
        if (dlg.ShowDialog() != true) return;

        IsExportRunning = true;
        ExportDone      = false;
        ExportStatus    = "Building bundle…";
        ExportProgress  = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExportService>();
            var progress = new Progress<ExportProgress>(p =>
            {
                ExportProgress = p.Total > 0 ? (int)((double)p.Processed / p.Total * 100) : 0;
                ExportStatus   = p.CurrentFile;
            });
            await svc.CreateBundleAsync(SelectedPhotoIds, dlg.FileName, progress);
            ExportProgress = 100;
            ExportStatus   = $"Bundle saved — {SelectedPhotoIds.Count} photo(s) exported.";
            ExportDone     = true;
        }
        catch (Exception ex)
        {
            ExportStatus = $"Error: {ex.Message}";
        }
        finally { IsExportRunning = false; }
    }

    private bool CanRunExport() => HasSelection && !IsExportRunning;

    // ── Import bundle ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseBundleFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title     = "Select Bundle",
            Filter    = "PhotoWell Bundle (*.pwbundle)|*.pwbundle",
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;
        BundleFilePath = dlg.FileName;
        _ = LoadBundleSummaryAsync(dlg.FileName);
    }

    [RelayCommand]
    private void BrowseBundleDestFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to place the imported photos",
        };
        if (dlg.ShowDialog() == true)
            BundleDestFolder = dlg.FolderName;
    }

    private async Task LoadBundleSummaryAsync(string path)
    {
        BundleSummary = "Reading bundle…";
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExportService>();
        var manifest = await svc.ReadBundleManifestAsync(path);
        BundleSummary = manifest == null
            ? "Not a valid PhotoWell bundle."
            : $"{manifest.PhotoCount} photos · exported {manifest.ExportedAt:yyyy-MM-dd} · from {manifest.SourceMachine}";
        OnPropertyChanged(nameof(HasBundleSummary));
        ImportBundleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleImport))]
    private async Task ImportBundleAsync()
    {
        if (string.IsNullOrEmpty(BundleDestFolder))
        {
            BundleImportStatus = "Please choose a destination folder first.";
            return;
        }

        IsBundleImportRunning = true;
        BundleImportDone      = false;
        BundleImportStatus    = "Starting import…";
        BundleImportProgress  = 0;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExportService>();
            var progress = new Progress<ExportProgress>(p =>
            {
                BundleImportProgress = p.Total > 0 ? (int)((double)p.Processed / p.Total * 100) : 0;
                BundleImportStatus   = p.CurrentFile;
            });

            var result = await svc.ImportBundleAsync(
                BundleFilePath, BundleDestFolder,
                resolveConflict: ShowConflictDialogAsync,
                progress: progress);

            BundleImportProgress = 100;
            BundleImportStatus   = BuildImportSummary(result);
            BundleImportDone     = true;
            BundleImportCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            BundleImportStatus = $"Error: {ex.Message}";
        }
        finally { IsBundleImportRunning = false; }
    }

    private bool CanRunBundleImport() => !string.IsNullOrEmpty(BundleFilePath)
                                      && File.Exists(BundleFilePath)
                                      && !BundleSummary.StartsWith("Not a valid")
                                      && !string.IsNullOrEmpty(BundleDestFolder)
                                      && !IsBundleImportRunning;

    partial void OnBundleFilePathChanged(string value)   => ImportBundleCommand.NotifyCanExecuteChanged();
    partial void OnBundleDestFolderChanged(string value) => ImportBundleCommand.NotifyCanExecuteChanged();

    private Task<ConflictResolution> ShowConflictDialogAsync(BundleConflict conflict)
    {
        // Run on UI thread
        var tcs = new TaskCompletionSource<ConflictResolution>();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var msg = $"Conflict: \"{conflict.FileName}\"\n\n" +
                      $"Your version:  rating {conflict.ExistingRating}, {conflict.ExistingTags.Count} tags\n" +
                      $"Bundle version: rating {conflict.BundleRating}, {conflict.BundleTags.Count} tags\n\n" +
                      "Which version do you want to keep?";
            var result = System.Windows.MessageBox.Show(
                msg, "Resolve Conflict",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);
            // Yes = Keep Existing, No = Use Bundle
            tcs.SetResult(result == System.Windows.MessageBoxResult.Yes
                ? ConflictResolution.KeepExisting
                : ConflictResolution.UseBundle);
        });
        return tcs.Task;
    }

    private static string BuildImportSummary(BundleImportResult r)
    {
        var parts = new List<string>();
        if (r.Imported > 0) parts.Add($"{r.Imported} imported");
        if (r.Merged   > 0) parts.Add($"{r.Merged} merged");
        if (r.Skipped  > 0) parts.Add($"{r.Skipped} skipped");
        if (r.Failed   > 0) parts.Add($"{r.Failed} failed");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "Nothing to import.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}
