namespace TPoolNet.Services;

using TPoolNet.Abstractions;

/// <summary>
/// Implementation of <see cref="IPooledTableLease"/> representing an active table lease.
/// For transient leases, scope disposal triggers automatic truncation and return to the pool.
/// For persistent leases, disposal is a no-op so that table contents survive application/stage boundaries.
/// </summary>
public sealed class PooledTableLease : IPooledTableLease
{
    private readonly Func<PooledTableLease, string, Task> _releaseAction;
    private int _disposed;

    public long TablePoolId { get; init; }
    public string SchemaName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string ConsumerId { get; init; } = string.Empty;
    public DateTime BookedAtUtc { get; init; }
    public DateTime? DeadlineUtc { get; init; }
    public bool IsPersistent => DeadlineUtc.HasValue;
    public string FullQualifiedName => $"[{SchemaName}].[{TableName}]";

    public PooledTableLease(Func<PooledTableLease, string, Task> releaseAction)
    {
        ArgumentNullException.ThrowIfNull(releaseAction);
        _releaseAction = releaseAction;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!IsPersistent)
        {
            // Transient leases auto-release on scope disposal
            await _releaseAction(this, "Disposed").ConfigureAwait(false);
        }
        // Persistent leases deliberately do NOT truncate or release on DisposeAsync.
    }
}
