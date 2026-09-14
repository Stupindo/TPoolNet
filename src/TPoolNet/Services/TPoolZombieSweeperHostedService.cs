namespace TPoolNet.Services;

using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TPoolNet.Data;
using TPoolNet.Options;

/// <summary>
/// Background hosted service that periodically scans for abandoned transient table leases (zombies)
/// and expired persistent table holds, truncating their physical tables and recording the reclamation
/// in [tpool].[TablesUsageHistory].
/// </summary>
public partial class TPoolZombieSweeperHostedService : BackgroundService
{
    private static readonly Regex ValidIdentifierRegex = ValidIdentifierCompiledRegex();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<TPoolOptions> _options;
    private readonly ILogger<TPoolZombieSweeperHostedService> _logger;

    private const string SelectCandidatesSql = @"
SELECT u.TablePoolId, p.SchemaName, p.TableName, u.ConsumerId, u.BookedAtUtc,
       CASE 
           WHEN u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME() THEN 'SweptExpired'
           ELSE 'SweptZombie'
       END AS CleanupReason
FROM [tpool].[TablesUsage] u WITH (UPDLOCK, READPAST)
JOIN [tpool].[TablesPool] p ON u.TablePoolId = p.TablePoolId
WHERE 
    (u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME())
    OR 
    (u.DeadlineUtc IS NULL AND u.HeartbeatUtc < DATEADD(second, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME()));
";

    public TPoolZombieSweeperHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<TPoolOptions> options,
        ILogger<TPoolZombieSweeperHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.EnableSweeper)
        {
            return;
        }

        var intervalSeconds = _options.Value.SweeperIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            intervalSeconds = 60;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogSweepIterationFailed(_logger, ex);
            }
        }
    }

    /// <summary>
    /// Executes a single sweep cycle using a new DI scope to detect and reclaim zombie transient leases
    /// and expired persistent leases.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tables reclaimed during this sweep.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TPoolDbContext>();
        return await SweepAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a single sweep cycle against the provided <see cref="TPoolDbContext"/> to detect and reclaim
    /// zombie transient leases and expired persistent leases.
    /// </summary>
    /// <param name="context">The database context to use for the sweep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tables reclaimed during this sweep.</returns>
    public async Task<int> SweepAsync(TPoolDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var heartbeatTimeoutSeconds = _options.Value.HeartbeatTimeoutSeconds;
        if (heartbeatTimeoutSeconds <= 0)
        {
            heartbeatTimeoutSeconds = 120;
        }

        var candidates = await FetchCandidatesAsync(context, heartbeatTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            LogSweepCompleted(_logger, 0);
            return 0;
        }

        var reclaimedCount = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var cleaned = await CleanSingleTableAsync(context, candidate, heartbeatTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (cleaned)
                {
                    reclaimedCount++;
                }
            }
            catch (Exception ex)
            {
                LogTableSweepFailed(_logger, ex, candidate.SchemaName, candidate.TableName, candidate.TablePoolId, candidate.ConsumerId);
            }
        }

        LogSweepCompleted(_logger, reclaimedCount);
        return reclaimedCount;
    }

    private static async Task<List<CandidateRecord>> FetchCandidatesAsync(
        TPoolDbContext context,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = SelectCandidatesSql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        var timeoutParam = new SqlParameter("@HeartbeatTimeoutSeconds", SqlDbType.Int)
        {
            Value = heartbeatTimeoutSeconds
        };
        command.Parameters.Add(timeoutParam);

        var candidates = new List<CandidateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new CandidateRecord
            {
                TablePoolId = reader.GetInt64(0),
                SchemaName = reader.GetString(1),
                TableName = reader.GetString(2),
                ConsumerId = reader.GetString(3),
                BookedAtUtc = reader.GetDateTime(4),
                CleanupReason = reader.GetString(5)
            });
        }

        return candidates;
    }

    private async Task<bool> CleanSingleTableAsync(
        TPoolDbContext context,
        CandidateRecord candidate,
        int heartbeatTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(candidate.SchemaName, nameof(candidate.SchemaName));
        ValidateIdentifier(candidate.TableName, nameof(candidate.TableName));

        var sql = string.Format(
            CultureInfo.InvariantCulture,
            @"
DECLARE @BookedAtUtc DATETIME2(2);
DECLARE @ActualReason VARCHAR(50);

SELECT @BookedAtUtc = BookedAtUtc,
       @ActualReason = CASE 
           WHEN DeadlineUtc IS NOT NULL AND DeadlineUtc < SYSUTCDATETIME() THEN 'SweptExpired'
           WHEN DeadlineUtc IS NULL AND HeartbeatUtc < DATEADD(second, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME()) THEN 'SweptZombie'
           ELSE NULL
       END
FROM [tpool].[TablesUsage] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
WHERE TablePoolId = @TablePoolId AND ConsumerId = @ConsumerId;

IF @ActualReason IS NOT NULL AND @BookedAtUtc IS NOT NULL
BEGIN
    TRUNCATE TABLE [{0}].[{1}];

    INSERT INTO [tpool].[TablesUsageHistory] (TablePoolId, ConsumerId, BookedAtUtc, ReleasedAtUtc, ReleaseReason)
    VALUES (@TablePoolId, @ConsumerId, @BookedAtUtc, SYSUTCDATETIME(), @ActualReason);

    DELETE FROM [tpool].[TablesUsage]
    WHERE TablePoolId = @TablePoolId AND ConsumerId = @ConsumerId;

    SELECT CAST(1 AS BIT) AS Cleaned, @ActualReason AS Reason;
END
ELSE
BEGIN
    SELECT CAST(0 AS BIT) AS Cleaned, CAST(NULL AS VARCHAR(50)) AS Reason;
END
",
            candidate.SchemaName,
            candidate.TableName);

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var existingTransaction = context.Database.CurrentTransaction;
        IDbContextTransaction? localTransaction = null;

        if (existingTransaction == null)
        {
            localTransaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = existingTransaction?.GetDbTransaction() ?? localTransaction?.GetDbTransaction();

            var poolIdParam = new SqlParameter("@TablePoolId", SqlDbType.BigInt) { Value = candidate.TablePoolId };
            var consumerParam = new SqlParameter("@ConsumerId", SqlDbType.VarChar, 100) { Value = candidate.ConsumerId };
            var timeoutParam = new SqlParameter("@HeartbeatTimeoutSeconds", SqlDbType.Int) { Value = heartbeatTimeoutSeconds };

            command.Parameters.Add(poolIdParam);
            command.Parameters.Add(consumerParam);
            command.Parameters.Add(timeoutParam);

            var cleaned = false;
            var finalReason = candidate.CleanupReason;

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cleaned = reader.GetBoolean(0);
                    if (cleaned && !reader.IsDBNull(1))
                    {
                        finalReason = reader.GetString(1);
                    }
                }
            }

            if (localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (cleaned)
            {
                LogTableSwept(_logger, candidate.SchemaName, candidate.TableName, candidate.TablePoolId, candidate.ConsumerId, finalReason);
            }

            return cleaned;
        }
        catch
        {
            if (localTransaction != null)
            {
                try
                {
                    await localTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Suppress secondary rollback exception to preserve primary exception
                }
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Swept table [{SchemaName}].[{TableName}] (PoolId: {TablePoolId}) for consumer '{ConsumerId}'. Reason: '{CleanupReason}'.")]
    private static partial void LogTableSwept(ILogger logger, string schemaName, string tableName, long tablePoolId, string consumerId, string cleanupReason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Completed table pool zombie sweep. Reclaimed {Count} tables.")]
    private static partial void LogSweepCompleted(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to sweep table [{SchemaName}].[{TableName}] (PoolId: {TablePoolId}) for consumer '{ConsumerId}'.")]
    private static partial void LogTableSweepFailed(ILogger logger, Exception ex, string schemaName, string tableName, long tablePoolId, string consumerId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Unexpected error occurred during table pool zombie sweep background iteration.")]
    private static partial void LogSweepIterationFailed(ILogger logger, Exception ex);

    private sealed class CandidateRecord
    {
        public long TablePoolId { get; init; }
        public string SchemaName { get; init; } = string.Empty;
        public string TableName { get; init; } = string.Empty;
        public string ConsumerId { get; init; } = string.Empty;
        public DateTime BookedAtUtc { get; init; }
        public string CleanupReason { get; init; } = string.Empty;
    }
}
