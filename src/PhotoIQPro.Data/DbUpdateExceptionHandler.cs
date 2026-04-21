using PhotoIQPro.Common;

namespace PhotoIQPro.Data;

/// <summary>
/// Consolidates error handling patterns for EntityFramework Core DbUpdateException.
/// </summary>
/// <remarks>
/// Several repositories catch DbUpdateException to:
/// 1. Build a full exception chain message (concatenate InnerExceptions)
/// 2. Log the error via AppLog.Error
/// 3. Restore entity state (Detached on Add failure, Unchanged on Delete failure)
/// 4. Re-throw the original exception
///
/// This helper eliminates code duplication and ensures consistent error logging and recovery.
/// </remarks>
public static class DbUpdateExceptionHandler
{
    /// <summary>
    /// Handles a DbUpdateException by logging the full exception chain and restoring entity state.
    /// </summary>
    /// <remarks>
    /// The exception chain message includes all nested InnerException messages, providing full context
    /// for debugging database constraint violations, concurrency conflicts, etc.
    /// </remarks>
    /// <param name="ex">The DbUpdateException that occurred.</param>
    /// <param name="operation">Description of the operation (e.g., "MediaFileRepository.AddAsync", "ExclusionRepository.RemoveAsync").</param>
    /// <param name="logMessage">Optional additional context to include in the log message.</param>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">The original exception is re-thrown after logging.</exception>
    public static void LogAndThrow(Exception ex, string operation, string? logMessage = null)
    {
        // Build the full exception chain message
        var chain = ex.Message;
        var inner = ex.InnerException;
        while (inner != null)
        {
            chain += " -> " + inner.Message;
            inner = inner.InnerException;
        }

        var message = logMessage != null
            ? $"{operation} failed: {logMessage} | {chain}"
            : $"{operation} failed: {chain}";

        AppLog.Error(message);
        throw ex;
    }
}
