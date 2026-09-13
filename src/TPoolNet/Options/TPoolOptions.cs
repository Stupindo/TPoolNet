namespace TPoolNet.Options;

/// <summary>
/// Configuration options for TPoolNet services.
/// </summary>
public class TPoolOptions
{
    /// <summary>
    /// The SQL Server connection string used to connect to the TPool database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The duration in seconds after which a transient lease without an updated heartbeat
    /// is considered abandoned and eligible for sweeper reclamation. Defaults to 120 seconds.
    /// </summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// The execution interval in seconds for the background zombie sweeper hosted service.
    /// Defaults to 60 seconds.
    /// </summary>
    public int SweeperIntervalSeconds { get; set; } = 60;
}
