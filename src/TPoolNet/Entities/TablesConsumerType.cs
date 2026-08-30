namespace TPoolNet.Entities;

/// <summary>
/// Reference lookup table for consumer categories (e.g., ETL job, reporting service).
/// Not used in the leasing path; retained for reporting and auditing purposes.
/// </summary>
public class TablesConsumerType
{
    public int ConsumerTypeId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;

    /// <summary>Optional human-readable description of the consumer category.</summary>
    public string? Description { get; set; }
}
