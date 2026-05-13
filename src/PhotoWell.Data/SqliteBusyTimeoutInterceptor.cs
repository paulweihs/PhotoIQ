using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PhotoWell.Common;

namespace PhotoWell.Data;

/// <summary>
/// Sets SQLite's busy_timeout on every connection as soon as it opens.
///
/// SQLite's default busy_timeout is 0 ms — a writer that finds the database
/// locked by another connection returns SQLITE_BUSY immediately, which EF Core
/// surfaces as DbUpdateException. Under normal app load several DbContext
/// instances write concurrently (FTS rebuild, vision worker, import queue,
/// folder watcher); a non-zero timeout lets them queue up instead of failing.
///
/// 5 000 ms is generous for typical desktop I/O.  The timeout applies per
/// SQLite API call, not per transaction, so a heavily-contested write will
/// retry automatically without any application-level retry logic.
/// </summary>
public sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    private readonly int _milliseconds;

    // Tracks how many SQLite connections are open simultaneously.
    private static int _openCount;
    private static long _nextConnId;

    // Stores a unique ID per connection object (keyed by identity, not equality).
    private static readonly ConditionalWeakTable<DbConnection, StrongBox<long>> _connIds = new();

    private static long GetConnId(DbConnection connection)
        => _connIds.GetOrCreateValue(connection).Value == 0
            ? (_connIds.GetOrCreateValue(connection).Value = System.Threading.Interlocked.Increment(ref _nextConnId))
            : _connIds.GetOrCreateValue(connection).Value;

    public SqliteBusyTimeoutInterceptor(int milliseconds = 5_000)
        => _milliseconds = milliseconds;

    private void ApplyPragmas(DbConnection connection)
    {
        // Run each PRAGMA as a separate command — Microsoft.Data.Sqlite only
        // executes the first statement when multiple are separated by semicolons.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout = {_milliseconds};";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }
    }

    private async Task ApplyPragmasAsync(DbConnection connection, CancellationToken ct)
    {
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout = {_milliseconds};";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        var id    = GetConnId(connection);
        var count = System.Threading.Interlocked.Increment(ref _openCount);
        AppLog.Info($"[DB] Connection #{id} opened (total open: {count}) thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken);
        var id    = GetConnId(connection);
        var count = System.Threading.Interlocked.Increment(ref _openCount);
        AppLog.Info($"[DB] Connection #{id} opened async (total open: {count}) thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        var id    = GetConnId(connection);
        var count = System.Threading.Interlocked.Decrement(ref _openCount);
        AppLog.Info($"[DB] Connection #{id} closed (total open: {count}) thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
    }

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        var id    = GetConnId(connection);
        var count = System.Threading.Interlocked.Decrement(ref _openCount);
        AppLog.Info($"[DB] Connection #{id} closed async (total open: {count}) thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
        return Task.CompletedTask;
    }
}
