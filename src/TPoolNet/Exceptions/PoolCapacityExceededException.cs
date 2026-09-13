namespace TPoolNet.Exceptions;

/// <summary>
/// Exception thrown when attempting to provision or configure table capacity that exceeds
/// the <see cref="TPoolNet.Entities.TablesType.MaxPoolSize"/> configured for the table type.
/// </summary>
public class PoolCapacityExceededException : InvalidOperationException
{
    /// <summary>
    /// Gets the table type name.
    /// </summary>
    public string TableTypeName { get; }

    /// <summary>
    /// Gets the number of tables requested to be provisioned.
    /// </summary>
    public int RequestedCount { get; }

    /// <summary>
    /// Gets the current number of tables in the pool for this table type.
    /// </summary>
    public int CurrentCount { get; }

    /// <summary>
    /// Gets the configured maximum pool size for this table type.
    /// </summary>
    public int MaxPoolSize { get; }

    public PoolCapacityExceededException(string tableTypeName, int requestedCount, int currentCount, int maxPoolSize)
        : base($"Cannot provision {requestedCount} table(s) for table type '{tableTypeName}'. Current count is {currentCount} and MaxPoolSize is {maxPoolSize}.")
    {
        TableTypeName = tableTypeName;
        RequestedCount = requestedCount;
        CurrentCount = currentCount;
        MaxPoolSize = maxPoolSize;
    }

    public PoolCapacityExceededException(string tableTypeName, int requestedCount, int currentCount, int maxPoolSize, string message)
        : base(message)
    {
        TableTypeName = tableTypeName;
        RequestedCount = requestedCount;
        CurrentCount = currentCount;
        MaxPoolSize = maxPoolSize;
    }

    public PoolCapacityExceededException(string tableTypeName, int requestedCount, int currentCount, int maxPoolSize, string message, Exception innerException)
        : base(message, innerException)
    {
        TableTypeName = tableTypeName;
        RequestedCount = requestedCount;
        CurrentCount = currentCount;
        MaxPoolSize = maxPoolSize;
    }
}
