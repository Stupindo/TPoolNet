namespace TPoolNet.Entities;

/// <summary>
/// Represents a registered physical pool table slot. Each row corresponds to one physical table
/// in SQL Server whose lifecycle is managed by TPoolNet.
/// </summary>
public class TablesPool
{
    public long TablePoolId { get; set; }
    public int TableTypeId { get; set; }

    /// <summary>Database schema that owns the physical table (default: "tpool").</summary>
    public string SchemaName { get; set; } = "tpool";

    /// <summary>Name of the physical SQL Server table (e.g., "tbl_Order_001").</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the table is eligible for leasing. Set to false to soft-decommission a table
    /// without dropping it.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>UTC timestamp when the table was registered into the pool.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public virtual TablesType TableType { get; set; } = null!;
    public virtual TablesUsage? ActiveUsage { get; set; }
}
