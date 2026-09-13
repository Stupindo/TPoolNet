namespace TPoolNet.Exceptions;

/// <summary>
/// Exception thrown when attempting to release a table lease with a consumer ID that does not match
/// the consumer currently holding the lease.
/// </summary>
public class ConsumerMismatchException : InvalidOperationException
{
    /// <summary>
    /// Gets the database schema of the table.
    /// </summary>
    public string SchemaName { get; }

    /// <summary>
    /// Gets the name of the pooled table.
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// Gets the consumer ID provided in the release request.
    /// </summary>
    public string ExpectedConsumerId { get; }

    /// <summary>
    /// Gets the actual consumer ID that holds the active lease.
    /// </summary>
    public string ActualConsumerId { get; }

    public ConsumerMismatchException(string schemaName, string tableName, string expectedConsumerId, string actualConsumerId)
        : base($"Consumer mismatch: table '[{schemaName}].[{tableName}]' is booked by '{actualConsumerId}', not '{expectedConsumerId}'.")
    {
        SchemaName = schemaName;
        TableName = tableName;
        ExpectedConsumerId = expectedConsumerId;
        ActualConsumerId = actualConsumerId;
    }

    public ConsumerMismatchException(string schemaName, string tableName, string expectedConsumerId, string actualConsumerId, string message)
        : base(message)
    {
        SchemaName = schemaName;
        TableName = tableName;
        ExpectedConsumerId = expectedConsumerId;
        ActualConsumerId = actualConsumerId;
    }

    public ConsumerMismatchException(string schemaName, string tableName, string expectedConsumerId, string actualConsumerId, string message, Exception innerException)
        : base(message, innerException)
    {
        SchemaName = schemaName;
        TableName = tableName;
        ExpectedConsumerId = expectedConsumerId;
        ActualConsumerId = actualConsumerId;
    }
}
