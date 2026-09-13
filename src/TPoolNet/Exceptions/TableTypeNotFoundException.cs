namespace TPoolNet.Exceptions;

/// <summary>
/// Exception thrown when an operation references a table type name that does not exist in [tpool].[TablesType].
/// </summary>
public class TableTypeNotFoundException : InvalidOperationException
{
    /// <summary>
    /// Gets the table type name that was not found.
    /// </summary>
    public string TableTypeName { get; }

    public TableTypeNotFoundException(string tableTypeName)
        : base($"Table type '{tableTypeName}' was not found.")
    {
        TableTypeName = tableTypeName;
    }

    public TableTypeNotFoundException(string tableTypeName, string message)
        : base(message)
    {
        TableTypeName = tableTypeName;
    }

    public TableTypeNotFoundException(string tableTypeName, string message, Exception innerException)
        : base(message, innerException)
    {
        TableTypeName = tableTypeName;
    }
}
