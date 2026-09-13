namespace TPoolNet.Services;

using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TPoolNet.Abstractions;
using TPoolNet.Data;
using TPoolNet.Entities;
using TPoolNet.Exceptions;

/// <summary>
/// Core implementation of <see cref="ITableProvisionerService"/> providing programmatic DDL-based table provisioning,
/// dynamic sequence naming, capacity guardrails, and table decommissioning.
/// </summary>
public partial class TableProvisionerService : ITableProvisionerService
{
    private static readonly Regex ValidIdentifierRegex = ValidIdentifierCompiledRegex();

    private readonly TPoolDbContext _context;
    private readonly ILogger<TableProvisionerService>? _logger;

    public TableProvisionerService(TPoolDbContext context, ILogger<TableProvisionerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ProvisionTablesAsync(
        string tableTypeName,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var tableType = await _context.TablesTypes
            .FirstOrDefaultAsync(t => t.TypeName == tableTypeName, cancellationToken)
            .ConfigureAwait(false);

        if (tableType == null)
        {
            throw new TableTypeNotFoundException(tableTypeName);
        }

        ValidateIdentifier(tableType.TablePrefix, nameof(tableType.TablePrefix));

        if (!tableType.DdlTemplate.Contains("{TABLENAME}", StringComparison.OrdinalIgnoreCase) &&
            !tableType.DdlTemplate.Contains("<TableName>", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"DDL template for table type '{tableTypeName}' does not contain '{{TABLENAME}}' placeholder.");
        }

        var currentCount = await _context.TablesPools
            .CountAsync(p => p.TableTypeId == tableType.TableTypeId, cancellationToken)
            .ConfigureAwait(false);

        if (currentCount + count > tableType.MaxPoolSize)
        {
            if (_logger != null)
            {
                LogCapacityExceeded(_logger, tableTypeName, count, currentCount, tableType.MaxPoolSize);
            }
            throw new PoolCapacityExceededException(tableTypeName, count, currentCount, tableType.MaxPoolSize);
        }

        const string schemaName = "tpool";
        ValidateIdentifier(schemaName, nameof(schemaName));

        // Retrieve all table names in this schema to prevent any naming collision
        var allSchemaTableNames = await _context.TablesPools
            .Where(p => p.SchemaName == schemaName)
            .Select(p => p.TableName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var prefixPattern = $"^tbl_{Regex.Escape(tableType.TablePrefix)}_(\\d+)$";
        var prefixRegex = new Regex(prefixPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var maxSequence = 0;
        foreach (var name in allSchemaTableNames)
        {
            var match = prefixRegex.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            {
                if (seq > maxSequence)
                {
                    maxSequence = seq;
                }
            }
        }

        var newTableNames = new List<string>(count);
        var existingSet = new HashSet<string>(allSchemaTableNames, StringComparer.OrdinalIgnoreCase);
        var seqCounter = maxSequence;

        while (newTableNames.Count < count)
        {
            seqCounter++;
            var candidate = $"tbl_{tableType.TablePrefix}_{seqCounter:D3}";
            if (!existingSet.Contains(candidate))
            {
                ValidateIdentifier(candidate, nameof(candidate));
                newTableNames.Add(candidate);
                existingSet.Add(candidate);
            }
        }

        var existingTransaction = _context.Database.CurrentTransaction;
        IDbContextTransaction? localTransaction = null;

        if (existingTransaction == null)
        {
            localTransaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var tableName in newTableNames)
            {
                var createSql = tableType.DdlTemplate
                    .Replace("{SCHEMA}", schemaName, StringComparison.OrdinalIgnoreCase)
                    .Replace("{TABLENAME}", tableName, StringComparison.OrdinalIgnoreCase)
                    .Replace("<SchemaName>", schemaName, StringComparison.OrdinalIgnoreCase)
                    .Replace("<TableName>", tableName, StringComparison.OrdinalIgnoreCase)
                    .Replace("<SCHEMA>", schemaName, StringComparison.OrdinalIgnoreCase)
                    .Replace("<TABLENAME>", tableName, StringComparison.OrdinalIgnoreCase);

                await _context.Database.ExecuteSqlRawAsync(createSql, cancellationToken).ConfigureAwait(false);

                var poolEntry = new TablesPool
                {
                    TableTypeId = tableType.TableTypeId,
                    SchemaName = schemaName,
                    TableName = tableName,
                    IsActive = true,
                    CreatedAtUtc = DateTime.UtcNow
                };

                _context.TablesPools.Add(poolEntry);

                if (_logger != null)
                {
                    LogTableProvisioned(_logger, schemaName, tableName, tableTypeName);
                }
            }

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
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

    /// <inheritdoc />
    public async Task DropTablesAsync(
        string tableTypeName,
        int count,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var tableType = await _context.TablesTypes
            .FirstOrDefaultAsync(t => t.TypeName == tableTypeName, cancellationToken)
            .ConfigureAwait(false);

        if (tableType == null)
        {
            throw new TableTypeNotFoundException(tableTypeName);
        }

        IQueryable<TablesPool> query = _context.TablesPools
            .Where(p => p.TableTypeId == tableType.TableTypeId);

        if (!force)
        {
            query = query.Where(p => !_context.TablesUsages.Any(u => u.TablePoolId == p.TablePoolId));
        }

        // Prioritize unused tables before booked tables when force is true, then newest (highest ID) first
        var tablesToDrop = await query
            .Include(p => p.ActiveUsage)
            .OrderBy(p => _context.TablesUsages.Any(u => u.TablePoolId == p.TablePoolId) ? 1 : 0)
            .ThenByDescending(p => p.TablePoolId)
            .Take(count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tablesToDrop.Count == 0)
        {
            return;
        }

        var existingTransaction = _context.Database.CurrentTransaction;
        IDbContextTransaction? localTransaction = null;

        if (existingTransaction == null)
        {
            localTransaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var table in tablesToDrop)
            {
                ValidateIdentifier(table.SchemaName, nameof(table.SchemaName));
                ValidateIdentifier(table.TableName, nameof(table.TableName));

                if (force && table.ActiveUsage != null)
                {
                    _context.TablesUsageHistories.Add(new TablesUsageHistory
                    {
                        TablePoolId = table.TablePoolId,
                        ConsumerId = table.ActiveUsage.ConsumerId,
                        BookedAtUtc = table.ActiveUsage.BookedAtUtc,
                        ReleasedAtUtc = DateTime.UtcNow,
                        ReleaseReason = "Dropped"
                    });
                    _context.TablesUsages.Remove(table.ActiveUsage);
                }

                var dropSql = $"DROP TABLE IF EXISTS [{table.SchemaName}].[{table.TableName}];";
                await _context.Database.ExecuteSqlRawAsync(dropSql, cancellationToken).ConfigureAwait(false);

                _context.TablesPools.Remove(table);

                if (_logger != null)
                {
                    LogTableDropped(_logger, table.SchemaName, table.TableName, tableTypeName, force);
                }
            }

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (localTransaction != null)
            {
                await localTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
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

    /// <inheritdoc />
    public async Task<int> EnsurePoolCapacityAsync(
        string tableTypeName,
        int targetCapacity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableTypeName);
        ArgumentOutOfRangeException.ThrowIfNegative(targetCapacity);

        var tableType = await _context.TablesTypes
            .FirstOrDefaultAsync(t => t.TypeName == tableTypeName, cancellationToken)
            .ConfigureAwait(false);

        if (tableType == null)
        {
            throw new TableTypeNotFoundException(tableTypeName);
        }

        var currentActiveCount = await _context.TablesPools
            .CountAsync(p => p.TableTypeId == tableType.TableTypeId && p.IsActive, cancellationToken)
            .ConfigureAwait(false);

        if (targetCapacity > tableType.MaxPoolSize)
        {
            throw new PoolCapacityExceededException(
                tableTypeName,
                requestedCount: targetCapacity - currentActiveCount,
                currentCount: currentActiveCount,
                maxPoolSize: tableType.MaxPoolSize,
                $"Target capacity {targetCapacity} exceeds MaxPoolSize {tableType.MaxPoolSize} for table type '{tableTypeName}'.");
        }

        if (currentActiveCount >= targetCapacity)
        {
            return 0;
        }

        var needed = targetCapacity - currentActiveCount;
        await ProvisionTablesAsync(tableTypeName, needed, cancellationToken).ConfigureAwait(false);
        return needed;
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

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Requested count {RequestedCount} exceeds available capacity for table type '{TableTypeName}'. Current count: {CurrentCount}, MaxPoolSize: {MaxPoolSize}.")]
    private static partial void LogCapacityExceeded(ILogger logger, string tableTypeName, int requestedCount, int currentCount, int maxPoolSize);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Provisioned physical table [{SchemaName}].[{TableName}] for table type '{TableTypeName}'.")]
    private static partial void LogTableProvisioned(ILogger logger, string schemaName, string tableName, string tableTypeName);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Dropped physical table [{SchemaName}].[{TableName}] for table type '{TableTypeName}'. Forced: {Force}.")]
    private static partial void LogTableDropped(ILogger logger, string schemaName, string tableName, string tableTypeName, bool force);
}
