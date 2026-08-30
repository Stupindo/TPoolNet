namespace TPoolNet.Entities;

/// <summary>
/// Represents a template type that defines the DDL shape, naming prefix, and pool size limit
/// for a category of pooled physical tables.
/// </summary>
public class TablesType
{
    public int TableTypeId { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string TablePrefix { get; set; } = string.Empty;

    /// <summary>
    /// DDL template with {SCHEMA} and {TABLENAME} placeholders used to create physical pool tables.
    /// </summary>
    public string DdlTemplate { get; set; } = string.Empty;

    public int MaxPoolSize { get; set; }

    public virtual ICollection<TablesPool> Tables { get; set; } = new List<TablesPool>();
}
