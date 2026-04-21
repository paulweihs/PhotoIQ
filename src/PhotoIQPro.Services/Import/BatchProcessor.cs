using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Services.Import;

/// <summary>
/// Generic batch processor for handling bulk operations with progress tracking, error collection, and batched persistence.
/// </summary>
/// <remarks>
/// Encapsulates the common pattern used in ReanalyzeAllAsync, BackfillExifAllAsync, and RegenerateThumbnailsAsync:
/// - Initialization with item count, batch size, and callbacks
/// - Per-item processing with success/failure/skip tracking
/// - Automatic batch flushing when full or at end
/// - Progress reporting after each item
/// - Final ImportResult with counts and errors
///
/// Usage:
/// ```csharp
/// var processor = new BatchProcessor<MediaFile>(
///     items: photos,
///     batchSize: 5,
///     flush: async batch => await _repo.BatchUpdateAsync(batch),
///     onBatchFlushed: batch => { /* notify UI */ },
///     progress: progressReporter);
///
/// foreach (var item in processor.Items)
/// {
///     try
///     {
///         DoSomething(item);
///         processor.Success(item);
///     }
///     catch (Exception ex)
///     {
///         processor.Failure(item, ex.Message);
///     }
/// }
///
/// var result = await processor.CompleteAsync();
/// ```
/// </remarks>
/// <typeparam name="T">The type of items being processed (typically MediaFile).</typeparam>
public class BatchProcessor<T>
{
    private readonly Func<List<T>, Task> _flushCallback;
    private readonly Action<List<T>>? _onBatchFlushed;
    private readonly IProgress<ImportProgress>? _progress;
    private readonly List<string> _errors = [];
    private readonly List<T> _batchBuffer;
    private readonly int _batchSize;
    private readonly DateTime _startTime;

    public IReadOnlyList<T> Items { get; }
    public int Total { get; }
    public int Processed { get; private set; }
    public int SuccessCount { get; private set; }
    public int SkipCount { get; private set; }
    public int FailCount { get; private set; }

    /// <summary>
    /// Initializes a new BatchProcessor for bulk operations.
    /// </summary>
    /// <param name="items">The collection of items to process.</param>
    /// <param name="batchSize">Number of items to batch before flushing to persistence (e.g., 5 or 10).</param>
    /// <param name="flush">Async callback to flush a batch to the database.</param>
    /// <param name="onBatchFlushed">Optional callback invoked after each batch is successfully flushed (for UI updates).</param>
    /// <param name="progress">Optional progress reporter to update the UI after each item.</param>
    public BatchProcessor(
        IReadOnlyList<T> items,
        int batchSize,
        Func<List<T>, Task> flush,
        Action<List<T>>? onBatchFlushed = null,
        IProgress<ImportProgress>? progress = null)
    {
        Items = items;
        Total = items.Count;
        _batchSize = batchSize;
        _batchBuffer = new List<T>(batchSize);
        _flushCallback = flush;
        _onBatchFlushed = onBatchFlushed;
        _progress = progress;
        _startTime = DateTime.UtcNow;
    }

    /// <summary>Marks an item as successfully processed and adds it to the batch buffer.</summary>
    public void Success(T item)
    {
        SuccessCount++;
        _batchBuffer.Add(item);
    }

    /// <summary>Marks an item as skipped (e.g., file not found, unsupported format).</summary>
    public void Skip()
    {
        SkipCount++;
    }

    /// <summary>Marks an item as failed with an error message.</summary>
    public void Failure(T item, string message)
    {
        FailCount++;
        _errors.Add($"{item}: {message}");
    }

    /// <summary>
    /// Reports progress and flushes the batch if it has reached capacity or this is the final item.
    /// Should be called once per item after calling Success/Skip/Failure.
    /// </summary>
    public async Task ReportAndFlushIfNeededAsync(string currentItemPath)
    {
        Processed++;
        _progress?.Report(new ImportProgress(Total, Processed, SuccessCount, SkipCount, FailCount, currentItemPath));

        // Flush when batch is full or this is the last item
        if (_batchBuffer.Count >= _batchSize || Processed == Total)
        {
            await FlushAsync();
        }
    }

    /// <summary>Immediately flushes any pending items in the batch buffer.</summary>
    private async Task FlushAsync()
    {
        if (_batchBuffer.Count == 0) return;
        await _flushCallback(_batchBuffer);
        _onBatchFlushed?.Invoke(_batchBuffer);
        _batchBuffer.Clear();
    }

    /// <summary>Completes processing and returns an ImportResult with final counts and errors.</summary>
    public async Task<ImportResult> CompleteAsync()
    {
        // Ensure any remaining items are flushed
        await FlushAsync();
        return new ImportResult(Total, SuccessCount, SkipCount, FailCount, DateTime.UtcNow - _startTime, _errors);
    }
}
