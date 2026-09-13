namespace TPoolNet.Services;

using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TPoolNet.Abstractions;
using TPoolNet.Data;
using TPoolNet.Exceptions;

/// <summary>
/// Core implementation of <see cref="ITablePoolService"/> providing high-throughput, deadlock-free
/// transient and persistent table leasing via atomic CTE reservation queries and transactional release.
/// </summary>
public partial class TablePoolService : ITablePoolService
{
    private static readonly Regex ValidIdentifierRegex = ValidIdentifierCompiledRegex();

    private readonly TPoolDbContext _context;
    private readonly ILogger<TablePoolService>? _logger;

    private const string BookSql = @"
DECLARE @Booked TABLE (
    TablePoolId BIGINT NOT NULL,
    ConsumerId VARCHAR(100) NOT NULL,
    BookedAtUtc DATETIME2(2) NOT NULL,
    DeadlineUtc DATETIME2(2) NULL
);

WITH AvailableTable AS (
    SELECT TOP (1) p.TablePoolId
    FROM [tpool].[TablesPool] p WITH (ROWLOCK, READPAST)
    INNER JOIN [tpool].[TablesType] t ON p.TableTypeId = t.TableTypeId
    WHERE t.TypeName = @TableTypeName 
      AND p.IsActive = 1
      AND NOT EXISTS (
          SELECT 1 
          FROM [tpool].[TablesUsage] u WITH (UPDLOCK, ROWLOCK, READPAST)
          WHERE u.TablePoolId = p.TablePoolId
      )
    ORDER BY p.TablePoolId ASC
)
INSERT INTO [tpool].[TablesUsage] (TablePoolId, ConsumerId, BookedAtUtc, HeartbeatUtc, DeadlineUtc)
OUTPUT 
    inserted.TablePoolId, 
    inserted.ConsumerId, 
    inserted.BookedAtUtc, 
    inserted.DeadlineUtc
INTO @Booked
SELECT TablePoolId, @ConsumerId, SYSUTCDATETIME(), SYSUTCDATETIME(), @DeadlineUtc
FROM AvailableTable;

SELECT 
    b.TablePoolId, 
    b.ConsumerId, 
    b.BookedAtUtc, 
    b.DeadlineUtc,
    p.SchemaName,
    p.TableName
FROM @Booked b
INNER JOIN [tpool].[TablesPool] p ON b.TablePoolId = p.TablePoolId;
";

    public TablePoolService(TPoolDbContext context, ILogger<TablePoolService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IPooledTableLease> BookAsync(
        string tableTypeName,
        string consumerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);

        return BookCoreAsync(tableTypeName, consumerId, deadlineUtc: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IPooledTableLease> BookPersistentAsync(
        string tableTypeName,
        string consumerId,
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);

        var maxRetention = TimeSpan.FromDays(365 * 10); // 10 years maximum retention
        if (retentionPeriod <= TimeSpan.Zero || retentionPeriod > maxRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionPeriod),
                retentionPeriod,
                $"Retention period must be greater than zero and less than or equal to {maxRetention.TotalDays} days.");
        }

        var deadlineUtc = DateTime.UtcNow.Add(retentionPeriod);
        return BookCoreAsync(tableTypeName, consumerId, deadlineUtc, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string tableName,
        string consumerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);

        var (schemaName, cleanTableName) = ParseTableIdentifier(tableName);

        var usageRecord = await _context.TablesUsages
            .AsNoTracking()
            .Include(u => u.TablePool)
            .FirstOrDefaultAsync(
                u => u.TablePool.SchemaName == schemaName && u.TablePool.TableName == cleanTableName,
                cancellationToken)
            .ConfigureAwait(false);

        if (usageRecord == null)
        {
            throw new TableLeaseNotFoundException(schemaName, cleanTableName);
        }

        if (!string.Equals(usageRecord.ConsumerId, consumerId, StringComparison.Ordinal))
        {
            throw new ConsumerMismatchException(schemaName, cleanTableName, consumerId, usageRecord.ConsumerId);
        }

        var released = await ReleaseInternalAsync(
            usageRecord.TablePoolId,
            schemaName,
            cleanTableName,
            consumerId,
            releaseReason: "Explicit",
            cancellationToken).ConfigureAwait(false);

        if (!released)
        {
            throw new TableLeaseNotFoundException(schemaName, cleanTableName);
        }
    }

    /// <inheritdoc />
    public async Task SendHeartbeatAsync(
        long tablePoolId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
UPDATE [tpool].[TablesUsage]
SET HeartbeatUtc = SYSUTCDATETIME()
WHERE TablePoolId = @TablePoolId;
";

        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

        var param = command.CreateParameter();
        param.ParameterName = "@TablePoolId";
        param.DbType = DbType.Int64;
        param.Value = tablePoolId;
        command.Parameters.Add(param);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (_logger != null)
        {
            LogHeartbeatSent(_logger, tablePoolId);
        }
    }

    private async Task<IPooledTableLease> BookCoreAsync(
        string tableTypeName,
        string consumerId,
        DateTime? deadlineUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);

        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = BookSql;
        command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

        var typeParam = new SqlParameter("@TableTypeName", SqlDbType.VarChar, 50) { Value = tableTypeName };
        var consumerParam = new SqlParameter("@ConsumerId", SqlDbType.VarChar, 100) { Value = consumerId };
        var deadlineParam = new SqlParameter("@DeadlineUtc", SqlDbType.DateTime2)
        {
            Value = (object?)deadlineUtc ?? DBNull.Value
        };

        command.Parameters.Add(typeParam);
        command.Parameters.Add(consumerParam);
        command.Parameters.Add(deadlineParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_logger != null)
            {
                LogPoolExhausted(_logger, tableTypeName, consumerId);
            }
            throw new TablePoolExhaustedException(tableTypeName);
        }

        var booked = new BookedTableResult
        {
            TablePoolId = reader.GetInt64(0),
            ConsumerId = reader.GetString(1),
            BookedAtUtc = reader.GetDateTime(2),
            DeadlineUtc = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            SchemaName = reader.GetString(4),
            TableName = reader.GetString(5)
        };

        if (_logger != null)
        {
            LogTableBooked(_logger, booked.SchemaName, booked.TableName, booked.TablePoolId, booked.ConsumerId, booked.DeadlineUtc.HasValue);
        }

        return new PooledTableLease(async (lease, reason) =>
        {
            await ReleaseInternalAsync(
                lease.TablePoolId,
                lease.SchemaName,
                lease.TableName,
                lease.ConsumerId,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        })
        {
            TablePoolId = booked.TablePoolId,
            SchemaName = booked.SchemaName,
            TableName = booked.TableName,
            ConsumerId = booked.ConsumerId,
            BookedAtUtc = booked.BookedAtUtc,
            DeadlineUtc = booked.DeadlineUtc
        };
    }

    private async Task<bool> ReleaseInternalAsync(
        long tablePoolId,
        string schemaName,
        string tableName,
        string consumerId,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(schemaName, nameof(schemaName));
        ValidateIdentifier(tableName, nameof(tableName));

        var sql = string.Format(
            CultureInfo.InvariantCulture,
            @"
DECLARE @BookedAtUtc DATETIME2(2);

SELECT @BookedAtUtc = BookedAtUtc
FROM [tpool].[TablesUsage] WITH (UPDLOCK, HOLDLOCK)
WHERE TablePoolId = @TablePoolId AND ConsumerId = @ConsumerId;

IF @BookedAtUtc IS NOT NULL
BEGIN
    TRUNCATE TABLE [{0}].[{1}];

    INSERT INTO [tpool].[TablesUsageHistory] (TablePoolId, ConsumerId, BookedAtUtc, ReleasedAtUtc, ReleaseReason)
    VALUES (@TablePoolId, @ConsumerId, @BookedAtUtc, SYSUTCDATETIME(), @ReleaseReason);

    DELETE FROM [tpool].[TablesUsage]
    WHERE TablePoolId = @TablePoolId;

    SELECT CAST(1 AS BIT) AS Released;
END
ELSE
BEGIN
    SELECT CAST(0 AS BIT) AS Released;
END
",
            schemaName,
            tableName);

        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var existingTransaction = _context.Database.CurrentTransaction;
        IDbContextTransaction? localTransaction = null;

        if (existingTransaction == null)
        {
            localTransaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = existingTransaction?.GetDbTransaction() ?? localTransaction?.GetDbTransaction();

            var poolIdParam = new SqlParameter("@TablePoolId", SqlDbType.BigInt) { Value = tablePoolId };
            var consumerParam = new SqlParameter("@ConsumerId", SqlDbType.VarChar, 100) { Value = consumerId };
            var reasonParam = new SqlParameter("@ReleaseReason", SqlDbType.VarChar, 50) { Value = releaseReason };

            command.Parameters.Add(poolIdParam);
            command.Parameters.Add(consumerParam);
            command.Parameters.Add(reasonParam);

            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var released = scalar is true or 1;

            if (localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (released && _logger != null)
            {
                LogTableReleased(_logger, schemaName, tableName, tablePoolId, consumerId, releaseReason);
            }

            return released;
        }
        catch
        {
            if (localTransaction != null)
            {
                await localTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (localTransaction != null)
            {
                await localTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static (string SchemaName, string TableName) ParseTableIdentifier(string tableName)
    {
        var parts = tableName.Replace("[", "", StringComparison.Ordinal)
                             .Replace("]", "", StringComparison.Ordinal)
                             .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            ValidateIdentifier(parts[0], nameof(tableName));
            return ("tpool", parts[0]);
        }

        if (parts.Length == 2)
        {
            ValidateIdentifier(parts[0], nameof(tableName));
            ValidateIdentifier(parts[1], nameof(tableName));
            return (parts[0], parts[1]);
        }

        throw new ArgumentException($"Invalid table identifier format '{tableName}'. Expected '[schema].[table]' or 'table'.", nameof(tableName));
    }

    private static void ValidateIdentifier(string identifier, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier, paramName);
        if (!ValidIdentifierRegex.IsMatch(identifier))
        {
            throw new ArgumentException($"Invalid SQL identifier '{identifier}'. Only alphanumeric characters and underscores are allowed.", paramName);
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidIdentifierCompiledRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Sent heartbeat for TablePoolId {TablePoolId}.")]
    private static partial void LogHeartbeatSent(ILogger logger, long tablePoolId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Pool exhausted for table type '{TableTypeName}'. Consumer: '{ConsumerId}'.")]
    private static partial void LogPoolExhausted(ILogger logger, string tableTypeName, string consumerId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Booked table [{SchemaName}].[{TableName}] (PoolId: {TablePoolId}) for consumer '{ConsumerId}'. Persistent: {IsPersistent}.")]
    private static partial void LogTableBooked(ILogger logger, string schemaName, string tableName, long tablePoolId, string consumerId, bool isPersistent);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Released table [{SchemaName}].[{TableName}] (PoolId: {TablePoolId}) for consumer '{ConsumerId}'. Reason: '{ReleaseReason}'.")]
    private static partial void LogTableReleased(ILogger logger, string schemaName, string tableName, long tablePoolId, string consumerId, string releaseReason);

    private sealed class BookedTableResult
    {
        public long TablePoolId { get; init; }
        public string ConsumerId { get; init; } = string.Empty;
        public DateTime BookedAtUtc { get; init; }
        public DateTime? DeadlineUtc { get; init; }
        public string SchemaName { get; init; } = string.Empty;
        public string TableName { get; init; } = string.Empty;
    }
}
