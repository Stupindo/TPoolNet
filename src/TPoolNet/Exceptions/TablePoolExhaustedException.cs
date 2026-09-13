namespace TPoolNet.Exceptions;

/// <summary>
/// Exception thrown when an attempt to lease a pooled table fails because no available, active tables
/// of the requested table type exist in the pool.
/// </summary>
public class TablePoolExhaustedException : InvalidOperationException
{
    /// <summary>
    /// Gets the table type name that was requested.
    /// </summary>
    public string TableTypeName { get; }

    public TablePoolExhaustedException(string tableTypeName)
        : base($"No available tables in the pool for table type '{tableTypeName}'.")
    {
        TableTypeName = tableTypeName;
    }

    public TablePoolExhaustedException(string tableTypeName, string message)
        : base(message)
    {
        TableTypeName = tableTypeName;
    }

    public TablePoolExhaustedException(string tableTypeName, string message, Exception innerException)
        : base(message, innerException)
    {
        TableTypeName = tableTypeName;
    }
}
