using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Import;

/// <summary>
/// Watches the distinct source folders already in the library and auto-imports any
/// new photo files that appear in them.
///
/// Design notes:
///   • Non-recursive per-folder watchers — covers the directories that already exist
///     in the DB; new top-level folder imports should call RefreshFoldersAsync().
///   • 1.5 s debounce per file path — file copy operations raise multiple Created/Renamed
///     events; we wait for the stream to go quiet before attempting to import.
///   • Each import runs in its own DI scope (IImportService is Scoped); the watcher
///     itself is registered as a Singleton.
///   • IImportService.ImportFileAsync handles deduplication, so racing with an active
///     manual import is harmless.
/// </summary>
public sealed class FolderWatcherService : IFolderWatcherService
{
    private const int DebounceMs = 1500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending
        = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _cts = new();
    private readonly object _scheduleLock = new();

    public Action<MediaFile>? OnPhotoImported { get; set; }

    public FolderWatcherService(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    public async Task StartAsync(CancellationToken ct = default)
        => await AddFoldersFromDbAsync(ct);

    public async Task RefreshFoldersAsync(CancellationToken ct = default)
        => await AddFoldersFromDbAsync(ct);

    private async Task AddFoldersFromDbAsync(CancellationToken ct)
    {
        IReadOnlyList<string> folders;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
            folders = await repo.GetDistinctFolderPathsAsync();
        }

        foreach (var folder in folders)
            TryAddWatcher(folder);
    }

    private void TryAddWatcher(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;
        if (_watchers.ContainsKey(folderPath)) return;
        if (_cts.IsCancellationRequested) return;

        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                NotifyFilter          = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents   = true
            };

            watcher.Created += OnFileCreated;
            watcher.Renamed += OnFileRenamed;

            if (_watchers.TryAdd(folderPath, watcher))
            {
                AppLog.Vision($"[Watcher] Monitoring: {folderPath}");
            }
            else
            {
                // Lost the race — another thread added it first.
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[Watcher] Could not watch '{folderPath}': {ex.Message}");
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e) => ScheduleImport(e.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs e)    => ScheduleImport(e.FullPath);

    private void ScheduleImport(string filePath)
    {
        if (!SupportedFormats.AllExtensions.Contains(
                Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            return;

        // Cancel any in-flight debounce for this path and restart the timer.
        // Lock ensures remove + add is atomic so two concurrent events for the same
        // path cannot both create a linked CTS and orphan one of them.
        CancellationTokenSource linked;
        lock (_scheduleLock)
        {
            if (_cts.IsCancellationRequested) return;
            if (_pending.TryRemove(filePath, out var prev))
            {
                prev.Cancel();
                prev.Dispose();
            }
            linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _pending[filePath] = linked;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, linked.Token);
                _pending.TryRemove(filePath, out _);

                using var scope = _scopeFactory.CreateScope();
                var import      = scope.ServiceProvider.GetRequiredService<IImportService>();
                var mf          = await import.ImportFileAsync(filePath, linked.Token);
                if (mf != null)
                    OnPhotoImported?.Invoke(mf);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLog.Error(
                    $"[Watcher] Import failed — {Path.GetFileName(filePath)}: " +
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }, linked.Token);
    }

    public void Stop()
    {
        // Cancel _cts under the schedule lock so ScheduleImport cannot create a new
        // linked CTS after we've begun tearing down.
        lock (_scheduleLock)
            _cts.Cancel();

        foreach (var w in _watchers.Values)
        {
            w.EnableRaisingEvents = false;
            w.Created -= OnFileCreated;
            w.Renamed -= OnFileRenamed;
            w.Dispose();
        }
        _watchers.Clear();

        // Drain _pending atomically: TryRemove in a loop so entries added between
        // two iterations (shouldn't happen after _cts is cancelled, but be safe) are caught.
        while (!_pending.IsEmpty)
        {
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
