namespace TPoolNet.Entities;

/// <summary>
/// Immutable audit record written when a leased table is released (for any reason).
/// Provides full history of table usage for diagnostics and capacity planning.
/// </summary>
public class TablesUsageHistory
{
    public long HistoryId { get; set; }
    public long TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime ReleasedAtUtc { get; set; }

    /// <summary>
    /// Why the table was released:
    /// <list type="bullet">
    ///   <item><term>Explicit</term><description>Manual call to ReleaseAsync.</description></item>
    ///   <item><term>Disposed</term><description>DisposeAsync on a transient lease.</description></item>
    ///   <item><term>SweptZombie</term><description>Sweeper reclaimed a stale transient lease (no heartbeat).</description></item>
    ///   <item><term>SweptExpired</term><description>Sweeper reclaimed an expired persistent lease (past DeadlineUtc).</description></item>
    /// </list>
    /// </summary>
    public string ReleaseReason { get; set; } = "Explicit";
}
