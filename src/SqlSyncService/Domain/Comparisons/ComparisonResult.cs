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

    /// <summary>
    /// Objects that were discovered during snapshot/build but excluded from
    /// comparison because their types are not in the supported whitelist
    /// (for example: logins, unknown/unclassified objects).
    /// </summary>
    public List<UnsupportedObject> UnsupportedObjects { get; set; } = new();
}

public class ComparisonSummary
{
    public int TotalDifferences { get; set; }
    public int Additions { get; set; }
    public int Modifications { get; set; }
    public int Deletions { get; set; }

    /// <summary>
    /// Number of objects that participated in comparison (after applying
    /// both the supported-type whitelist and ComparisonOptions flags).
    /// </summary>
    public int ObjectsCompared { get; set; }

    /// <summary>
    /// Number of objects that were compared and found to have no
    /// differences between database and project.
    /// </summary>
    public int ObjectsUnchanged { get; set; }

    /// <summary>
    /// Count of database-side objects whose types are not supported for
    /// comparison (including Unknown).
    /// </summary>
    public int UnsupportedDatabaseObjectCount { get; set; }

    /// <summary>
    /// Count of file-side objects whose types are not supported for
    /// comparison (including Unknown).
    /// </summary>
    public int UnsupportedFileObjectCount { get; set; }

    public Dictionary<string, int> ByObjectType { get; set; } = new();
}

public enum ComparisonStatus
{
    Synchronized,
    HasDifferences,
    Error,
    Partial
}

