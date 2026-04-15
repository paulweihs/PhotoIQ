using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Desktop.ViewModels;

/// <summary>One folder waiting in the persistent import queue.</summary>
internal record ImportQueueEntry(string Path, bool Recursive);

public partial class ImportProgressViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int    _total;
    [ObservableProperty] private int    _processed;
    [ObservableProperty] private int    _imported;
    [ObservableProperty] private int    _skipped;
    [ObservableProperty] private int    _failed;
    [ObservableProperty] private string _currentFile   = "";
    [ObservableProperty] private bool   _isComplete;
    [ObservableProperty] private bool   _isRunning;
    /// <summary>True only during queue-based file imports (RunFromQueueAsync / RunImportSelectionAsync).
    /// False for analysis, re-analysis, and thumbnail operations — those don't auto-resume on next launch.</summary>
    [ObservableProperty] private bool   _isQueueImport;
    /// <summary>True when the running operation persists its state and will auto-resume on next launch
    /// (currently: RunReanalyzeOutdatedAsync). Used to show the correct exit dialog message.</summary>
    [ObservableProperty] private bool   _isResumable;
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private string _heading        = "";
    [ObservableProperty] private string _importedLabel  = "Imported";
    [ObservableProperty] private string _skippedLabel   = "Already in library";

    /// <summary>Called on each progress tick with a value in [0, 1]. Set by the caller (MainViewModel) to drive the taskbar indicator.</summary>
    public Action<double>?  TaskbarProgress { get; set; }

    /// <summary>Called on each progress tick with the live batch rate text. Set by MainViewModel to update the status bar.</summary>
    public Action<string>?    OnBatchTick      { get; set; }

    /// <summary>Called after each photo completes analysis. Set by MainViewModel to update the gallery grid live.</summary>
    public Action<MediaFile>? OnPhotoAnalyzed  { get; set; }

    /// <summary>Called when Express library limit is hit mid-import. Set by MainViewModel to show the upgrade prompt.</summary>
    public Action<int>? OnExpressLimitReached { get; set; }

    private System.Diagnostics.Stopwatch? _batchTimer;

    // ── Import queue ─────────────────────────────────────────────────────────
    // Persisted to disk so a crash leaves the queue intact for the next startup.

    [ObservableProperty] private int _queuedFolderCount;

    private readonly SemaphoreSlim _queueFileLock = new(1, 1);

    /// <summary>
    /// Reads the queue file. Caller must hold <see cref="_queueFileLock"/>.
    /// Errors are logged and return an empty list (fail-open: the operation continues with no queue).
    /// </summary>
    private List<ImportQueueEntry> ReadQueueFileLocked()
    {
        try
        {
            if (!File.Exists(AppSettings.ImportQueuePath)) return [];
            var json = File.ReadAllText(AppSettings.ImportQueuePath);
            return JsonSerializer.Deserialize<List<ImportQueueEntry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to read import queue: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Writes the queue file. Caller must hold <see cref="_queueFileLock"/>.
    /// Errors are logged; the operation continues even if the write fails.
    /// </summary>
    private void WriteQueueFileLocked(List<ImportQueueEntry> entries)
    {
        try
        {
            if (entries.Count == 0)
                File.Delete(AppSettings.ImportQueuePath);
            else
                File.WriteAllText(AppSettings.ImportQueuePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to save import queue: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Atomically reads, mutates, and writes the queue file under a single lock acquisition,
    /// eliminating any TOCTOU window between the read and write. Returns the queue state
    /// after the mutation is applied.
    /// </summary>
    private async Task<List<ImportQueueEntry>> MutateQueueAsync(Action<List<ImportQueueEntry>> mutate)
    {
        await _queueFileLock.WaitAsync();
        try
        {
            var queue = ReadQueueFileLocked();
            mutate(queue);
            WriteQueueFileLocked(queue);
            return queue;
        }
        finally { _queueFileLock.Release(); }
    }

    /// <summary>Reads the queue file under the lock without modifying it.</summary>
    private async Task<List<ImportQueueEntry>> ReadQueueAsync()
    {
        await _queueFileLock.WaitAsync();
        try { return ReadQueueFileLocked(); }
        finally { _queueFileLock.Release(); }
    }

    /// <summary>
    /// Appends <paramref name="entries"/> to the on-disk queue and refreshes
    /// <see cref="QueuedFolderCount"/>. Safe to call while a queue worker is running.
    /// The read-modify-write is performed atomically under a single lock acquisition.
    /// </summary>
    public async Task EnqueueFoldersAsync(IEnumerable<string> paths, bool recursive)
    {
        var queue = await MutateQueueAsync(q =>
        {
            foreach (var p in paths)
                q.Add(new ImportQueueEntry(p, recursive));
        });
        QueuedFolderCount = queue.Count;
    }

    /// <summary>Returns the number of folders currently in the on-disk queue.</summary>
    public async Task<int> GetQueueCountAsync() =>
        (await ReadQueueAsync()).Count;

    private async Task RemoveFirstFromQueueAsync()
    {
        var queue = await MutateQueueAsync(q => { if (q.Count > 0) q.RemoveAt(0); });
        QueuedFolderCount = queue.Count;
    }

    /// <summary>Clears the persistent import queue. Called when the user declines to resume on startup.</summary>
    public async Task ClearQueueAsync()
    {
        await MutateQueueAsync(q => q.Clear());
        QueuedFolderCount = 0;
    }

    // ── Queue worker ─────────────────────────────────────────────────────────

    /// <summary>
    /// Drains the persistent import queue, processing one folder at a time.
    /// A folder is removed from the queue only after its import fully completes,
    /// so a crash mid-folder causes a retry on the next startup — already-imported
    /// files are skipped via ExistsAsync.
    ///
    /// Can be called concurrently: returns immediately if already running.
    /// Callers that want to add more folders while this is running should call
    /// <see cref="EnqueueFoldersAsync"/> — the worker picks them up on its next iteration.
    /// </summary>
    public async Task RunFromQueueAsync()
    {
        if (IsRunning) return;

        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        IsQueueImport = true;
        Heading       = "Importing Photos";
        ImportedLabel = "Imported";
        SkippedLabel  = "Already in library";
        CurrentFile   = "Starting…";
        _batchTimer   = System.Diagnostics.Stopwatch.StartNew();

        int totalImported = 0, totalSkipped = 0, totalFailed = 0;
        var allErrors = new List<string>();

        try
        {
            while (true)
            {
                var queue = await ReadQueueAsync();
                if (queue.Count == 0) break;

                QueuedFolderCount = queue.Count;
                var entry = queue[0];
                CurrentFile = Path.GetFileName(entry.Path);

                var progress = new Progress<ImportProgress>(p =>
                {
                    Total       = p.Total;
                    Processed   = p.Processed;
                    Imported    = totalImported + p.Imported;
                    Skipped     = totalSkipped  + p.Skipped;
                    Failed      = totalFailed   + p.Failed;
                    CurrentFile = Path.GetFileName(p.CurrentFile);
                    OnPropertyChanged(nameof(ProgressPercent));
                    OnPropertyChanged(nameof(StatusLine));
                });

                using var scopeQ = _scopeFactory.CreateScope();
                var importQ = scopeQ.ServiceProvider.GetRequiredService<IImportService>();
                var result = await importQ.ImportFolderAsync(entry.Path, entry.Recursive,
                    progress, OnPhotoAnalyzed, _cts.Token);
                allErrors.AddRange(result.Errors);

                totalImported = Imported;
                totalSkipped  = Skipped;
                totalFailed   = Failed;

                // Only remove from disk after the folder fully completes.
                await RemoveFirstFromQueueAsync();

                if (result.LimitReached)
                {
                    // Clear the queue — no point retrying when the limit is already hit.
                    await ClearQueueAsync();
                    OnExpressLimitReached?.Invoke(totalImported);
                    break;
                }
            }

            ResultMessage = BuildResultMessage(totalImported, totalSkipped, totalFailed,
                "imported", "already in library", allErrors);
        }
        catch (OperationCanceledException)
        {
            var remaining = (await ReadQueueAsync()).Count;
            ResultMessage = remaining > 0
                ? $"Cancelled — {totalImported} imported. {remaining} folder{(remaining == 1 ? "" : "s")} saved for next launch."
                : $"Cancelled — {totalImported} imported before stopping.";
            // Queue is intentionally NOT cleared — remaining folders resume on next startup.
        }
        catch (Exception ex)
        {
            var chain = BuildExceptionChain(ex);
            AppLog.Error($"Import queue worker crashed: {chain}\n{ex.StackTrace}");
            ResultMessage = $"Error: {chain}";
        }
        finally
        {
            IsComplete = true;
            IsRunning  = false;
            CurrentFile = "";
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public double ProgressPercent => Total > 0 ? (double)Processed / Total * 100 : 0;
    public string StatusLine      => Total > 0
        ? $"{Processed} of {Total}  ·  {Imported} imported  ·  {Skipped} skipped  ·  {Failed} failed"
        : "Preparing…";

    public string BatchRateText
    {
        get
        {
            if (_batchTimer == null || Processed == 0) return "";
            var elapsed = _batchTimer.Elapsed;
            var perMin  = elapsed.TotalMinutes > 0 ? Processed / elapsed.TotalMinutes : 0;
            var avgSec  = elapsed.TotalSeconds / Processed;
            return $"{Processed} analyzed  ·  {perMin:F1}/min  ·  avg {avgSec:F1}s/photo";
        }
    }

    public ImportProgressViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    partial void OnTotalChanged(int value)     => TaskbarProgress?.Invoke(Total > 0 ? (double)Processed / Total : 0);
    partial void OnProcessedChanged(int value)
    {
        TaskbarProgress?.Invoke(Total > 0 ? (double)value / Total : 0);
        OnPropertyChanged(nameof(BatchRateText));
        if (BatchRateText.Length > 0) OnBatchTick?.Invoke(BatchRateText);
    }

    /// <summary>Cancels without showing a confirmation dialog. Used by OnClosing after the user has already confirmed exit.</summary>
    public void ForceCancel() => _cts?.Cancel();

    [RelayCommand]
    private void CancelOrClose()
    {
        if (IsRunning)
        {
            string title, message;
            if (IsQueueImport)
            {
                title   = "Cancel Import";
                message = "Cancel the import?\n\n" +
                          "Photos imported so far will remain in your library. " +
                          "Remaining folders will be saved and will resume automatically next time you launch PhotoIQ Pro.";
            }
            else
            {
                title   = "Cancel Operation";
                message = $"Cancel {Heading}?\n\n" +
                          "Progress so far will be kept. The operation will not resume automatically.";
            }

            var result = System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);

            if (result != System.Windows.MessageBoxResult.Yes) return;
        }
        _cts?.Cancel();
    }

    private void Reset()
    {
        Total         = 0;
        Processed     = 0;
        Imported      = 0;
        Skipped       = 0;
        Failed        = 0;
        CurrentFile   = "";
        IsComplete    = false;
        IsQueueImport = false;
        IsResumable   = false;
        ResultMessage = "";
        _batchTimer   = null;
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(BatchRateText));
    }

    public async Task RunReanalyzeAsync(bool unanalyzedOnly = false, bool skipUserModified = false)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        Heading       = unanalyzedOnly ? "Analyzing Unanalyzed Photos" : "Re-analyzing All Photos";
        ImportedLabel = "Re-analyzed";
        SkippedLabel  = "Skipped";
        CurrentFile   = "Loading library…";
        _batchTimer   = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ImportProgress>(p =>
        {
            Total       = p.Total;
            Processed   = p.Processed;
            Imported    = p.Imported;
            Skipped     = p.Skipped;
            Failed      = p.Failed;
            CurrentFile = Path.GetFileName(p.CurrentFile);
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        });
        AppLog.Info($"[OP] RunReanalyzeAsync start (unanalyzedOnly={unanalyzedOnly}, skipUserModified={skipUserModified})");
        using var scope1 = _scopeFactory.CreateScope();
        var import1 = scope1.ServiceProvider.GetRequiredService<IImportService>();
        try
        {
            var result = await import1.ReanalyzeAllAsync(unanalyzedOnly, skipUserModified, progress, OnPhotoAnalyzed, _cts.Token);
            AppLog.Info($"[OP] RunReanalyzeAsync complete: imported={Imported} skipped={Skipped} failed={Failed}");
            ResultMessage = BuildResultMessage(Imported, Skipped, Failed,
                "re-analyzed", "skipped", result.Errors);
        }
        catch (OperationCanceledException) { ResultMessage = $"Cancelled — {Imported} re-analyzed before stopping."; }
        catch (Exception ex)
        {
            var chain = BuildExceptionChain(ex);
            AppLog.Error($"[OP] RunReanalyzeAsync EXCEPTION: {chain}\n{ex.StackTrace}");
            ResultMessage = $"Error: {chain}";
        }
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; OnPropertyChanged(nameof(StatusLine)); }
    }

    public async Task RunReanalyzeOutdatedAsync(bool skipUserModified = false)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        IsResumable   = true;
        Heading       = "Updating Outdated Descriptions";
        ImportedLabel = "Updated";
        SkippedLabel  = "Skipped";
        CurrentFile   = "Loading…";
        _batchTimer   = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ImportProgress>(p =>
        {
            Total       = p.Total;
            Processed   = p.Processed;
            Imported    = p.Imported;
            Skipped     = p.Skipped;
            Failed      = p.Failed;
            CurrentFile = Path.GetFileName(p.CurrentFile);
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        });
        AppLog.Info($"[OP] RunReanalyzeOutdatedAsync start (skipUserModified={skipUserModified})");
        // Write resume flag so the app can auto-restart this operation if the user exits mid-run.
        // The flag stores the skipUserModified setting so it can be reproduced exactly.
        await File.WriteAllTextAsync(AppSettings.UpdateOutdatedFlagPath, skipUserModified ? "1" : "0");
        using var scope2 = _scopeFactory.CreateScope();
        var import2 = scope2.ServiceProvider.GetRequiredService<IImportService>();
        bool completedNormally = false;
        try
        {
            var result = await import2.ReanalyzeOutdatedAsync(skipUserModified, progress, OnPhotoAnalyzed, _cts.Token);
            AppLog.Info($"[OP] RunReanalyzeOutdatedAsync complete: imported={Imported} skipped={Skipped} failed={Failed}");
            ResultMessage = BuildResultMessage(Imported, Skipped, Failed,
                "updated", "skipped", result.Errors,
                successOverride: $"{Imported} descriptions updated to {AppSettings.CurrentPromptVersion}");
            completedNormally = true;
        }
        catch (OperationCanceledException) { ResultMessage = $"Cancelled — {Imported} updated before stopping."; }
        catch (Exception ex)
        {
            var chain = BuildExceptionChain(ex);
            AppLog.Error($"[OP] RunReanalyzeOutdatedAsync EXCEPTION: {chain}\n{ex.StackTrace}");
            ResultMessage = $"Error: {chain}";
        }
        finally
        {
            IsComplete = true; IsRunning = false; CurrentFile = ""; OnPropertyChanged(nameof(StatusLine));
            // Only delete the flag on clean completion — leave it for interrupted/crashed runs so the
            // next launch can resume. Cancellation by the user is treated as interrupted (flag stays).
            if (completedNormally)
                try { File.Delete(AppSettings.UpdateOutdatedFlagPath); } catch { }
        }
    }

    public async Task RunReanalyzeOneAsync(Guid mediaFileId, string fileName)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        Heading       = "Re-analyzing Photo";
        ImportedLabel = "Re-analyzed";
        SkippedLabel  = "Skipped";
        CurrentFile   = fileName;
        Total         = 1;
        var progress = new Progress<ImportProgress>(p =>
        {
            Processed   = p.Processed;
            Imported    = p.Imported;
            Failed      = p.Failed;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        });
        using var scope3 = _scopeFactory.CreateScope();
        var import3 = scope3.ServiceProvider.GetRequiredService<IImportService>();
        try
        {
            await import3.ReanalyzeFileAsync(mediaFileId, progress, _cts.Token);
            ResultMessage = Imported > 0 ? "Re-analysis complete" : "Re-analysis produced no results";
        }
        catch (OperationCanceledException) { ResultMessage = "Cancelled."; }
        catch (Exception ex)               { ResultMessage = $"Error: {ex.Message}"; }
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; OnPropertyChanged(nameof(StatusLine)); }
    }

    public async Task RunReanalyzeSelectedAsync(IReadOnlyList<MediaFile> files)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        Heading       = $"Re-analyzing {files.Count} Selected Photos";
        ImportedLabel = "Re-analyzed";
        SkippedLabel  = "Skipped";
        Total         = files.Count;
        int reanalyzed = 0, failed = 0;
        using var scopeS = _scopeFactory.CreateScope();
        var importS = scopeS.ServiceProvider.GetRequiredService<IImportService>();
        for (int i = 0; i < files.Count; i++)
        {
            if (_cts.IsCancellationRequested) break;
            CurrentFile = files[i].FileName;
            try
            {
                var ok = await importS.ReanalyzeFileAsync(files[i].Id, null, _cts.Token);
                if (ok) reanalyzed++; else Skipped++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error($"Re-analyze failed for '{files[i].FileName}': {ex.GetType().Name}: {ex.Message}");
                failed++; Failed = failed;
            }
            Processed = i + 1;
            Imported  = reanalyzed;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        }
        ResultMessage = failed > 0
            ? $"{reanalyzed} re-analyzed  ·  {Skipped} skipped  ·  {failed} failed"
            : $"{reanalyzed} of {files.Count} photos re-analyzed successfully";
        IsComplete  = true;
        IsRunning   = false;
        CurrentFile = "";
        OnPropertyChanged(nameof(StatusLine));
    }

    public async Task RunRegenerateThumbnailsAsync(Action<PhotoIQPro.Core.Models.MediaFile>? onThumbnailRegenerated = null)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        Heading       = "Regenerating Thumbnails";
        ImportedLabel = "Updated";
        SkippedLabel  = "File not found";
        CurrentFile   = "Loading library…";
        var progress  = new Progress<ImportProgress>(p =>
        {
            Total       = p.Total;
            Processed   = p.Processed;
            Imported    = p.Imported;
            Skipped     = p.Skipped;
            Failed      = p.Failed;
            CurrentFile = Path.GetFileName(p.CurrentFile);
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        });
        using var scopeT = _scopeFactory.CreateScope();
        var importT = scopeT.ServiceProvider.GetRequiredService<IImportService>();
        try
        {
            var result = await importT.RegenerateThumbnailsAsync(progress, onThumbnailRegenerated, _cts.Token);
            if (Failed > 0)
            {
                var sample = result.Errors.Take(5).Select(e => $"• {e}");
                var more   = result.Errors.Count > 5 ? $"\n…and {result.Errors.Count - 5} more" : "";
                ResultMessage = $"{Imported} updated  ·  {Skipped} not found  ·  {Failed} failed\n\n{string.Join("\n", sample)}{more}";
            }
            else
            {
                ResultMessage = $"{Imported} thumbnails regenerated";
            }
        }
        catch (OperationCanceledException) { ResultMessage = $"Cancelled — {Imported} updated before stopping."; }
        catch (Exception ex)               { ResultMessage = $"Error: {ex.Message}"; }
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; OnPropertyChanged(nameof(StatusLine)); }
    }


    public async Task RunAsync(string folder)
    {
        await EnqueueFoldersAsync([folder], recursive: true);
        await RunFromQueueAsync();
    }

    /// <summary>
    /// Import a mixed selection of folders and/or individual files from the folder picker.
    /// Folders are enqueued in the persistent import queue (resume-safe).
    /// Individual files are imported inline — they're an explicit one-off selection.
    /// </summary>
    public async Task RunImportSelectionAsync(
        IReadOnlyList<string> folders,
        IReadOnlyList<string> files,
        bool recursive = true)
    {
        // Enqueue all folders — the queue worker handles them with crash recovery.
        if (folders.Count > 0)
            await EnqueueFoldersAsync(folders, recursive);

        // Individual files: run immediately (not folder-based, no queue needed).
        if (files.Count > 0 && !IsRunning)
        {
            Reset();
            _cts          = new CancellationTokenSource();
            IsRunning     = true;
            IsQueueImport = true;
            Heading       = "Importing Photos";
            ImportedLabel = "Imported";
            SkippedLabel  = "Already in library";
            CurrentFile   = "Starting…";
            Total         = files.Count;
            _batchTimer   = System.Diagnostics.Stopwatch.StartNew();

            int imported = 0, skipped = 0, failed = 0;
            using var scopeF = _scopeFactory.CreateScope();
            var importF = scopeF.ServiceProvider.GetRequiredService<IImportService>();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (_cts.IsCancellationRequested) break;
                    CurrentFile = Path.GetFileName(files[i]);
                    try
                    {
                        var mf = await importF.ImportFileAsync(files[i], _cts.Token);
                        if (mf != null) { imported++; Imported = imported; OnPhotoAnalyzed?.Invoke(mf); }
                        else            { skipped++;  Skipped  = skipped;  }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { failed++; Failed = failed; }
                    Processed = i + 1;
                    OnPropertyChanged(nameof(ProgressPercent));
                    OnPropertyChanged(nameof(StatusLine));
                }
                ResultMessage = failed > 0
                    ? $"{imported} imported  ·  {skipped} already in library  ·  {failed} failed"
                    : $"{imported} imported  ·  {skipped} already in library";
            }
            catch (OperationCanceledException) { ResultMessage = $"Cancelled — {imported} imported before stopping."; }
            catch (Exception ex)               { ResultMessage = $"Error: {ex.Message}"; }
            finally { IsComplete = true; IsRunning = false; CurrentFile = ""; OnPropertyChanged(nameof(StatusLine)); }
        }

        // Now drain the folder queue (no-op if already running from a concurrent import).
        await RunFromQueueAsync();
    }

    /// <summary>
    /// Enqueues <paramref name="folders"/> and runs the queue worker.
    /// If a worker is already running (mid-import addition), the new folders are
    /// appended to the on-disk queue and picked up automatically; this call returns
    /// immediately in that case so the caller is not double-awaited.
    /// </summary>
    public async Task RunMultiFolderAsync(IReadOnlyList<string> folders, bool recursive = false)
    {
        await EnqueueFoldersAsync(folders, recursive);
        await RunFromQueueAsync();
    }

    /// <summary>
    /// Flattens an exception's InnerException chain into a single readable string.
    /// </summary>
    private static string BuildExceptionChain(Exception ex)
    {
        var sb = new System.Text.StringBuilder(ex.GetType().Name + ": " + ex.Message);
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            sb.Append(" → " + inner.GetType().Name + ": " + inner.Message);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a ResultMessage string. When there are failures and error details are available,
    /// appends a bulleted sample (up to 5) so the user can see which files were affected.
    /// </summary>
    private static string BuildResultMessage(
        int imported, int skipped, int failed,
        string importedLabel, string skippedLabel,
        List<string> errors,
        string? successOverride = null)
    {
        if (failed > 0)
        {
            var summary = $"{imported} {importedLabel}  ·  {skipped} {skippedLabel}  ·  {failed} failed";
            if (errors.Count > 0)
            {
                var sample = string.Join("\n", errors.Take(5).Select(e => $"• {e}"));
                var more   = errors.Count > 5 ? $"\n…and {errors.Count - 5} more" : "";
                return $"{summary}\n\n{sample}{more}";
            }
            return summary;
        }
        return successOverride ?? $"{imported} {importedLabel}  ·  {skipped} {skippedLabel}";
    }
}
