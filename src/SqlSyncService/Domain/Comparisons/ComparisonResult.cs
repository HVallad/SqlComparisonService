namespace SqlSyncService.Domain.Comparisons;

public class ComparisonResult
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public DateTime ComparedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public ComparisonStatus Status { get; set; }
    public List<SchemaDifference> Differences { get; set; } = new();
    public ComparisonSummary Summary { get; set; } = new();
}

public class ComparisonSummary
{
    public int TotalDifferences { get; set; }
    public int Additions { get; set; }
    public int Modifications { get; set; }
    public int Deletions { get; set; }
    public Dictionary<string, int> ByObjectType { get; set; } = new();
}

public enum ComparisonStatus
{
    Synchronized,
    HasDifferences,
    Error,
    Partial
}

