namespace TPoolNet.Exceptions;

/// <summary>
/// Exception thrown when attempting to release a table that has no active lease in [tpool].[TablesUsage].
/// </summary>
public class TableLeaseNotFoundException : InvalidOperationException
{
    /// <summary>
    /// Gets the database schema of the table.
    /// </summary>
    public string SchemaName { get; }

    /// <summary>
    /// Gets the name of the pooled table.
    /// </summary>
    public string TableName { get; }

    public TableLeaseNotFoundException(string schemaName, string tableName)
        : base($"No active lease found for table '[{schemaName}].[{tableName}]'.")
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    public TableLeaseNotFoundException(string schemaName, string tableName, string message)
        : base(message)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    public TableLeaseNotFoundException(string schemaName, string tableName, string message, Exception innerException)
        : base(message, innerException)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }
}
