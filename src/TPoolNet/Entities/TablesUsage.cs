namespace TPoolNet.Entities;

/// <summary>
/// Tracks the current active lease on a pool table. A row exists here only while the table is booked.
/// Uses the same PK as <see cref="TablesPool"/> (1:1 relationship — a pool slot has at most one active lease).
/// </summary>
/// <remarks>
/// <para><c>DeadlineUtc IS NULL</c> → Transient lease (auto-released via heartbeat sweeper or DisposeAsync).</para>
/// <para><c>DeadlineUtc IS NOT NULL</c> → Persistent lease (held until explicit release or deadline expiry).</para>
/// </remarks>
public class TablesUsage
{
    /// <summary>Shared PK with <see cref="TablesPool"/> — enforces the 1:1 relationship.</summary>
    public long TablePoolId { get; set; }

    /// <summary>Opaque string identifying the booking consumer (e.g., "etl-job-42", "reporting-svc").</summary>
    public string ConsumerId { get; set; } = string.Empty;

    public DateTime BookedAtUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }

    /// <summary>
    /// NULL for transient leases. Non-null for persistent leases — the table is reclaimed after this deadline.
    /// </summary>
    public DateTime? DeadlineUtc { get; set; }

    public virtual TablesPool TablePool { get; set; } = null!;
}
