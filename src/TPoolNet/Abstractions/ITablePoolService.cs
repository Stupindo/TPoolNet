namespace TPoolNet.Abstractions;

/// <summary>
/// Core service managing atomic leasing, explicit release, and heartbeat updates for pooled physical tables.
/// </summary>
public interface ITablePoolService
{
    /// <summary>
    /// Atomically books an available transient table of the specified table type for the consumer.
    /// The returned lease automatically releases and truncates the table when disposed.
    /// </summary>
    /// <param name="tableTypeName">The logical name of the table type defined in [tpool].[TablesType].</param>
    /// <param name="consumerId">The unique identifier of the consumer leasing the table.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An active transient table lease implementing <see cref="IAsyncDisposable"/>.</returns>
    /// <exception cref="TPoolNet.Exceptions.TablePoolExhaustedException">Thrown when no active, unbooked tables of the requested type are available.</exception>
    Task<IPooledTableLease> BookAsync(
        string tableTypeName,
        string consumerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically books an available persistent table of the specified table type for the consumer with a retention deadline.
    /// Persistent leases survive disposal and must be explicitly released via <see cref="ReleaseAsync"/>.
    /// </summary>
    /// <param name="tableTypeName">The logical name of the table type defined in [tpool].[TablesType].</param>
    /// <param name="consumerId">The unique identifier of the consumer leasing the table.</param>
    /// <param name="retentionPeriod">Duration from current UTC time before the persistent lease expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An active persistent table lease.</returns>
    /// <exception cref="TPoolNet.Exceptions.TablePoolExhaustedException">Thrown when no active, unbooked tables of the requested type are available.</exception>
    Task<IPooledTableLease> BookPersistentAsync(
        string tableTypeName,
        string consumerId,
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly releases a table lease, verifying consumer identity, truncating the physical table,
    /// recording the release in [tpool].[TablesUsageHistory], and removing the record from [tpool].[TablesUsage].
    /// </summary>
    /// <param name="tableName">The physical table name (with or without schema prefix).</param>
    /// <param name="consumerId">The identifier of the consumer holding the lease.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TPoolNet.Exceptions.TableLeaseNotFoundException">Thrown when no active lease exists for the specified table.</exception>
    /// <exception cref="TPoolNet.Exceptions.ConsumerMismatchException">Thrown when the consumer holding the lease does not match the provided consumer identifier.</exception>
    Task ReleaseAsync(
        string tableName,
        string consumerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the heartbeat timestamp for an active transient table lease to prevent premature sweeper reclamation.
    /// </summary>
    /// <param name="tablePoolId">The unique identifier of the table pool record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendHeartbeatAsync(
        long tablePoolId,
        CancellationToken cancellationToken = default);
}
