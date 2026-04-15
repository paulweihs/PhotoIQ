using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Core.Interfaces;

/// <summary>
/// Watches library source folders for new photo files and auto-imports them
/// without requiring explicit user action.
/// </summary>
public interface IFolderWatcherService : IDisposable
{
    /// <summary>
    /// Called on the thread pool when a new photo is successfully imported by the watcher.
    /// Wire this to the gallery refresh callback before calling StartAsync.
    /// </summary>
    Action<MediaFile>? OnPhotoImported { get; set; }

    /// <summary>
    /// Queries the library DB for all distinct source folders and begins watching each one.
    /// Safe to call multiple times — already-watched folders are skipped.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Ensures any folders not yet watched (e.g. new subfolders from a recent import)
    /// are added to the watch set. Re-queries the DB.
    /// </summary>
    Task RefreshFoldersAsync(CancellationToken ct = default);

    /// <summary>Stops all watchers and cancels any pending debounced imports.</summary>
    void Stop();
}
