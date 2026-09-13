namespace TPoolNet.Abstractions;

/// <summary>
/// Service responsible for dynamic provisioning and decommissioning of physical pool tables
/// in SQL Server based on <see cref="TPoolNet.Entities.TablesType"/> templates and pool capacity requirements.
/// </summary>
public interface ITableProvisionerService
{
    /// <summary>
    /// Dynamically provisions the requested count of physical tables in SQL Server using the DDL template
    /// associated with the specified table type, and registers the tables in [tpool].[TablesPool].
    /// </summary>
    /// <param name="tableTypeName">The unique name of the table type to provision.</param>
    /// <param name="count">The number of physical tables to provision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ProvisionTablesAsync(
        string tableTypeName,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the specified number of physical tables for the table type and unregisters them from [tpool].[TablesPool].
    /// By default, only unbooked (unused) tables are dropped. When <paramref name="force"/> is true, actively booked
    /// tables can also be dropped.
    /// </summary>
    /// <param name="tableTypeName">The unique name of the table type.</param>
    /// <param name="count">The number of tables to drop.</param>
    /// <param name="force">If true, drops tables even if they are currently leased/in-use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DropTablesAsync(
        string tableTypeName,
        int count,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that the table pool has at least <paramref name="targetCapacity"/> tables for the given table type.
    /// If the current pool capacity is less than <paramref name="targetCapacity"/>, the missing tables are dynamically provisioned.
    /// If the capacity is already met or exceeded, this method is a no-op.
    /// </summary>
    /// <param name="tableTypeName">The unique name of the table type.</param>
    /// <param name="targetCapacity">The desired minimum table count in the pool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of new tables provisioned (0 if capacity was already met).</returns>
    Task<int> EnsurePoolCapacityAsync(
        string tableTypeName,
        int targetCapacity,
        CancellationToken cancellationToken = default);
}
