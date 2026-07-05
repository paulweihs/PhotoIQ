using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>One folder waiting in the persistent import queue.</summary>
internal record ImportQueueEntry(string Path, bool Recursive);

public partial class ImportProgressViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource? _cts;

    /// <summary>Photos to be processed ahead of the current batch during Update Outdated.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _batchPriorityQueue = new();

    /// <summary>
    /// Enqueues a photo to be processed next in the running Update Outdated batch.
    /// Has no effect if Update Outdated is not currently running.
    /// </summary>
    public void EnqueuePriorityReanalysis(Guid photoId) => _batchPriorityQueue.Enqueue(photoId);

    [ObservableProperty] private int    _total;
    [ObservableProperty] private int    _processed;
    [ObservableProperty] private int    _imported;
    [ObservableProperty] private int    _skipped;
    [ObservableProperty] private int    _failed;
    [ObservableProperty] private string _currentFile   = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentThumbnail))]
    private BitmapSource? _currentThumbnail;

    [ObservableProperty] private bool _alwaysShowThumbnail;

    public bool HasCurrentThumbnail => CurrentThumbnail != null;

    private string _currentFilePath = "";
    public string CurrentFilePath => _currentFilePath;

    public string CurrentDirDisplay =>
        string.IsNullOrEmpty(_currentFilePath) ? ""
        : (System.IO.Path.GetDirectoryName(_currentFilePath) ?? "");

    public string CurrentFileNameDisplay =>
        string.IsNullOrEmpty(_currentFilePath) ? ""
        : ("\\" + System.IO.Path.GetFileName(_currentFilePath));

    private void SetCurrentFilePath(string value)
    {
        _currentFilePath = value;
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentDirDisplay));
        OnPropertyChanged(nameof(CurrentFileNameDisplay));
    }

    private int _thumbGen;

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

    /// <summary>Total supported files across all queued folders — populated by a background pre-scan at import start.
    /// Zero until the scan completes its first folder; grows as each queued folder is counted.</summary>
    [ObservableProperty] private int _grandTotal;
    [ObservableProperty] private int _grandExcluded;
    /// <summary>Files processed across all folders combined (resets at the start of each import job, not per-folder).
    /// Used for correct avg/ETA when multiple folders are queued.</summary>
    [ObservableProperty] private int _grandProcessed;

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
    /// Incremented each time a new RunFromQueueAsync run begins.
    /// The finally block checks this to avoid clearing IsRunning when a newer run has taken over.
    /// </summary>
    private int _runGeneration = 0;

    // ── Pause gate ───────────────────────────────────────────────────────────
    // SemaphoreSlim(1,1) used as a binary gate: 1 = open (running), 0 = closed (paused).
    // WaitIfPausedAsync acquires then immediately releases — passes through when open, blocks when closed.
    // TogglePauseCommand acquires to close the gate, releases to open it.
    private readonly SemaphoreSlim _pauseGate = new(1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonLabel))]
    private bool _isPaused;

    public string PauseButtonLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>Awaits the pause gate. Pass-through when running; blocks until resumed.</summary>
    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        await _pauseGate.WaitAsync(ct);
        _pauseGate.Release();
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (!IsPaused)
        {
            // Acquire the permit — closes the gate. Near-instant because workers release immediately.
            await _pauseGate.WaitAsync();
            IsPaused = true;
        }
        else
        {
            IsPaused = false;
            _pauseGate.Release();
        }
    }

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
    /// <param name="forceStart">
    /// When true, bypasses the IsRunning guard. Used by ResumeImportQueueAsync after it
    /// has already cancelled the previous operation and waited for it to stop.
    /// The generation counter ensures the old operation's finally block does not clear
    /// IsRunning for this new run even if it fires concurrently.
    /// </param>
    public async Task RunFromQueueAsync(bool forceStart = false)
    {
        AppLog.Info($"[QUEUE] RunFromQueueAsync entry: IsRunning={IsRunning}, forceStart={forceStart}");
        if (!forceStart && IsRunning)
        {
            AppLog.Info("[QUEUE] RunFromQueueAsync: IsRunning=true, forceStart=false — returning immediately");
            return;
        }

        int myGen = Interlocked.Increment(ref _runGeneration);
        AppLog.Info($"[QUEUE] RunFromQueueAsync: starting (generation={myGen})");

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
        int grandProcessedOffset = 0, grandExcludedOffset = 0;
        var allErrors = new List<string>();

        // Background pre-scan: count all supported files across every queued folder so we can show
        // "X of Y" as a job-wide total. Updates GrandTotal progressively as each folder is counted.
        var preScanCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                var allExtensions = SupportedFormats.AllExtensions;
                var q = await ReadQueueAsync();
                int runningTotal = 0;
                foreach (var e in q)
                {
                    preScanCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var searchOption = e.Recursive
                            ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly;
                        runningTotal += Directory.EnumerateFiles(e.Path, "*.*", searchOption)
                            .Count(f => allExtensions.Contains(Path.GetExtension(f)));
                        GrandTotal = runningTotal;
                    }
                    catch { /* skip inaccessible folders */ }
                }
            }
            catch (OperationCanceledException) { }
        }, preScanCts.Token);

        try
        {
            while (true)
            {
                await WaitIfPausedAsync(_cts.Token);
                var queue = await ReadQueueAsync();
                if (queue.Count == 0) break;

                QueuedFolderCount = queue.Count;
                var entry = queue[0];
                CurrentFile = Path.GetFileName(entry.Path);

                var progress = new Progress<ImportProgress>(p =>
                {
                    Total          = p.Total;
                    Processed      = p.Processed;
                    Imported       = totalImported + p.Imported;
                    Skipped        = totalSkipped  + p.Skipped;
                    Failed         = totalFailed   + p.Failed;
                    GrandProcessed = grandProcessedOffset + p.Processed;
                    GrandExcluded  = grandExcludedOffset  + p.Excluded;
                    SetCurrentFilePath(p.CurrentFile);
                    CurrentFile    = Path.GetFileName(p.CurrentFile);
                    _ = LoadThumbnailAsync(p.CurrentFile, ++_thumbGen);
                    OnPropertyChanged(nameof(ProgressPercent));
                });

                // Skip folders whose drive is offline — dequeue and continue rather than
                // crashing the whole run. The folder is removed so the queue advances.
                var driveRoot = Path.GetPathRoot(entry.Path);
                if (!string.IsNullOrEmpty(driveRoot) && !Directory.Exists(driveRoot))
                {
                    AppLog.Info($"[QUEUE] Skipping offline drive folder: {entry.Path}");
                    allErrors.Add($"Skipped (drive offline): {entry.Path}");
                    await RemoveFirstFromQueueAsync();
                    continue;
                }

                using var scopeQ = _scopeFactory.CreateScope();
                var importQ = scopeQ.ServiceProvider.GetRequiredService<IImportService>();
                ImportResult result;
                try
                {
                    result = await importQ.ImportFolderAsync(entry.Path, entry.Recursive,
                        progress, OnPhotoAnalyzed, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        msg += " -> " + inner.Message;
                    AppLog.Error($"[QUEUE] ImportFolderAsync failed for {entry.Path}: {msg}");
                    allErrors.Add($"{Path.GetFileName(entry.Path)}: {msg}");
                    await RemoveFirstFromQueueAsync();
                    continue;
                }
                allErrors.AddRange(result.Errors);

                totalImported        = Imported;
                totalSkipped         = Skipped;
                totalFailed          = Failed;
                grandProcessedOffset = GrandProcessed;
                grandExcludedOffset  = GrandExcluded;

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
            preScanCts.Cancel();
            preScanCts.Dispose();
            IsComplete  = true;
            // Only clear IsRunning if we are still the current generation.
            // If forceStart was used and a newer run has already taken over, leave IsRunning=true.
            if (Volatile.Read(ref _runGeneration) == myGen)
            {
                IsRunning = false;
                AppLog.Info($"[QUEUE] RunFromQueueAsync: finished (generation={myGen}), IsRunning cleared");
            }
            else
            {
                AppLog.Info($"[QUEUE] RunFromQueueAsync: finished (generation={myGen}) — superseded by generation {_runGeneration}, IsRunning left as-is");
            }
            CurrentFile = "";
            SetCurrentFilePath("");
            CurrentThumbnail = null;
            OnPropertyChanged(nameof(StatusLine));
        }
    }

    public double ProgressPercent => Total > 0 ? (double)Processed / Total * 100 : 0;

    public string StatusLine
    {
        get
        {
            if (GrandTotal > 0)
                return $"{GrandProcessed:N0} of {GrandTotal:N0}  ·  {Imported:N0} imported  ·  {Skipped:N0} skipped  ·  {Failed} failed";
            if (Total > 0)
                return $"{Processed:N0} of {Total:N0}  ·  {Imported:N0} imported  ·  {Skipped:N0} skipped  ·  {Failed} failed";
            return "Preparing…";
        }
    }

    public string BatchRateText
    {
        get
        {
            // Use grand (cross-folder) counts when available; fall back to per-folder for single-folder ops.
            // Exclude instantly-skipped excluded-folder files from rate — they involve no real work
            // and would inflate the files/min figure and deflate the avg s/photo figure.
            var processed    = GrandProcessed > 0 ? GrandProcessed : Processed;
            var excluded     = GrandExcluded;
            var countForRate = Math.Max(0, processed - excluded);
            var totalForRate = Math.Max(0, (GrandTotal > 0 ? GrandTotal : Total) - excluded);
            if (_batchTimer == null || countForRate == 0) return "";
            var elapsed   = _batchTimer.Elapsed;
            var perMin    = elapsed.TotalMinutes > 0 ? countForRate / elapsed.TotalMinutes : 0;
            var avgSec    = elapsed.TotalSeconds / countForRate;
            var remaining = totalForRate - countForRate;
            var etaPart   = remaining > 0 && avgSec > 0
                ? "  ·  ETA " + FormatEta(TimeSpan.FromSeconds(remaining * avgSec))
                : "";
            return $"{perMin:F1}/min  ·  avg {avgSec:F1}s/photo{etaPart}";
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return $"{(int)eta.TotalHours}h {eta.Minutes:D2}m";
        if (eta.TotalMinutes >= 1)
            return $"{(int)eta.TotalMinutes}m {eta.Seconds:D2}s";
        return $"{(int)eta.TotalSeconds}s";
    }

    public ImportProgressViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    partial void OnTotalChanged(int value)     => TaskbarProgress?.Invoke(Total > 0 ? (double)Processed / Total : 0);
    partial void OnProcessedChanged(int value)
    {
        TaskbarProgress?.Invoke(Total > 0 ? (double)value / Total : 0);
        // Only recalculate batch rate here for non-queue ops (no GrandProcessed).
        // Queue imports update via OnGrandProcessedChanged after GrandProcessed is set.
        if (GrandProcessed == 0)
        {
            OnPropertyChanged(nameof(BatchRateText));
            OnPropertyChanged(nameof(HasBatchRateText));
            if (BatchRateText.Length > 0) OnBatchTick?.Invoke(BatchRateText);
        }
    }

    partial void OnGrandProcessedChanged(int value)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(BatchRateText));
        OnPropertyChanged(nameof(HasBatchRateText));
        if (BatchRateText.Length > 0) OnBatchTick?.Invoke(BatchRateText);
    }

    partial void OnGrandTotalChanged(int value) => OnPropertyChanged(nameof(StatusLine));

    public bool HasBatchRateText => BatchRateText.Length > 0;

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
                          "Remaining folders will be saved and will resume automatically next time you launch PhotoWell.";
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

    private async Task LoadThumbnailAsync(string path, int gen)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (_thumbGen == gen) CurrentThumbnail = null;
            return;
        }

        byte[]? bytes;
        if (AlwaysShowThumbnail)
        {
            bytes = await Task.Run(() => { try { return File.ReadAllBytes(path); } catch { return null; } });
        }
        else
        {
            var readTask = Task.Run(() => { try { return File.ReadAllBytes(path); } catch { return (byte[]?)null; } });
            var winner = await Task.WhenAny(readTask, Task.Delay(200));
            bytes = winner == readTask ? readTask.Result : null;
        }

        if (_thumbGen != gen) return;

        BitmapSource? bmp = null;
        if (bytes != null)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = ms;
                img.DecodePixelWidth = 48;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
            catch { }
        }

        if (_thumbGen == gen) CurrentThumbnail = bmp;
    }

    private void Reset()
    {
        Total          = 0;
        Processed      = 0;
        Imported       = 0;
        Skipped        = 0;
        Failed         = 0;
        GrandTotal     = 0;
        GrandProcessed = 0;
        GrandExcluded  = 0;
        CurrentFile    = "";
        IsComplete     = false;
        IsQueueImport  = false;
        IsResumable    = false;
        ResultMessage  = "";
        _batchTimer    = null;
        while (_batchPriorityQueue.TryDequeue(out _)) { } // clear leftover priorities from previous run
        CurrentThumbnail = null;
        SetCurrentFilePath("");
        _thumbGen++;
        if (IsPaused) { IsPaused = false; _pauseGate.Release(); }
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(BatchRateText));
        OnPropertyChanged(nameof(HasBatchRateText));
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
            SetCurrentFilePath(p.CurrentFile);
            CurrentFile = Path.GetFileName(p.CurrentFile);
            _ = LoadThumbnailAsync(p.CurrentFile, ++_thumbGen);
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
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; SetCurrentFilePath(""); CurrentThumbnail = null; OnPropertyChanged(nameof(StatusLine)); }
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
            SetCurrentFilePath(p.CurrentFile);
            CurrentFile = Path.GetFileName(p.CurrentFile);
            _ = LoadThumbnailAsync(p.CurrentFile, ++_thumbGen);
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
            var result = await import2.ReanalyzeOutdatedAsync(skipUserModified, progress, OnPhotoAnalyzed, _cts.Token, _batchPriorityQueue);
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
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; SetCurrentFilePath(""); CurrentThumbnail = null; OnPropertyChanged(nameof(StatusLine)); }
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
            SetCurrentFilePath(files[i].FilePath);
            CurrentFile = files[i].FileName;
            _ = LoadThumbnailAsync(files[i].FilePath, ++_thumbGen);
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
        IsComplete       = true;
        IsRunning        = false;
        CurrentFile      = "";
        SetCurrentFilePath("");
        CurrentThumbnail = null;
        OnPropertyChanged(nameof(StatusLine));
    }

    public async Task RunRegenerateThumbnailsAsync(Action<PhotoWell.Core.Models.MediaFile>? onThumbnailRegenerated = null, bool resume = false)
    {
        Reset();
        _cts          = new CancellationTokenSource();
        IsRunning     = true;
        Heading       = resume ? "Resuming Thumbnail Regeneration" : "Regenerating Thumbnails";
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
            SetCurrentFilePath(p.CurrentFile);
            CurrentFile = Path.GetFileName(p.CurrentFile);
            _ = LoadThumbnailAsync(p.CurrentFile, ++_thumbGen);
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(StatusLine));
        });
        using var scopeT = _scopeFactory.CreateScope();
        var importT = scopeT.ServiceProvider.GetRequiredService<IImportService>();
        try
        {
            var result = await importT.RegenerateThumbnailsAsync(progress, onThumbnailRegenerated, _cts.Token, resume);
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
        finally { IsComplete = true; IsRunning = false; CurrentFile = ""; SetCurrentFilePath(""); CurrentThumbnail = null; OnPropertyChanged(nameof(StatusLine)); }
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
                    SetCurrentFilePath(files[i]);
                    CurrentFile = Path.GetFileName(files[i]);
                    _ = LoadThumbnailAsync(files[i], ++_thumbGen);
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
            finally { IsComplete = true; IsRunning = false; CurrentFile = ""; SetCurrentFilePath(""); CurrentThumbnail = null; OnPropertyChanged(nameof(StatusLine)); }
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
