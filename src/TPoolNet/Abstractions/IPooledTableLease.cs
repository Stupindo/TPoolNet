namespace TPoolNet.Abstractions;

/// <summary>
/// Represents an active lease on a pooled physical table.
/// Transient leases automatically release and truncate the table when disposed.
/// Persistent leases survive disposal and must be released explicitly via <see cref="ITablePoolService.ReleaseAsync"/>.
/// </summary>
public interface IPooledTableLease : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of the pooled table record in [tpool].[TablesPool].
    /// </summary>
    long TablePoolId { get; }

    /// <summary>
    /// Gets the database schema name of the physical pooled table.
    /// </summary>
    string SchemaName { get; }

    /// <summary>
    /// Gets the database table name of the physical pooled table.
    /// </summary>
    string TableName { get; }

    /// <summary>
    /// Gets the bracket-quoted, schema-qualified name of the physical table (e.g. <c>[tpool].[tbl_Order_001]</c>).
    /// </summary>
    string FullQualifiedName { get; }

    /// <summary>
    /// Gets the identifier of the consumer holding this lease.
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Gets the UTC timestamp when the table was booked.
    /// </summary>
    DateTime BookedAtUtc { get; }

    /// <summary>
    /// Gets the optional UTC deadline after which persistent leases become eligible for sweeper reclamation.
    /// <c>null</c> indicates a transient lease.
    /// </summary>
    DateTime? DeadlineUtc { get; }

    /// <summary>
    /// Gets a value indicating whether this lease is persistent (<see cref="DeadlineUtc"/> has value).
    /// </summary>
    bool IsPersistent { get; }
}
