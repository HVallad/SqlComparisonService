namespace SqlSyncService.Contracts.Comparisons;

public sealed class TriggerComparisonResponse
{
    public Guid ComparisonId { get; set; }
    public Guid SubscriptionId { get; set; }
    public string Status { get; set; } = "queued";
    public DateTime QueuedAt { get; set; }
    public string EstimatedDuration { get; set; } = "PT0S";
}

public sealed class GetSubscriptionComparisonsResponse
{
    public List<SubscriptionComparisonListItemResponse> Comparisons { get; set; } = new();
    public int TotalCount { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
}

public sealed class SubscriptionComparisonListItemResponse
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "completed";
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Duration { get; set; } = string.Empty;
    public int DifferenceCount { get; set; }
    public int ObjectsCompared { get; set; }
    public string Trigger { get; set; } = "manual";
}

public sealed class ComparisonDetailResponse
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string Status { get; set; } = "completed";
    public DateTime ComparedAt { get; set; }
    public string Duration { get; set; } = string.Empty;
    public int DifferenceCount { get; set; }
    public ComparisonSummaryResponse Summary { get; set; } = new();
}

public sealed class ComparisonSummaryResponse
{
    public int TotalDifferences { get; set; }

    public int ObjectsCompared { get; set; }

    public int ObjectsUnchanged { get; set; }

    /// <summary>
    /// Count of database-side objects that were detected but not
    /// compared because their types are not in the supported
    /// whitelist (for example: logins, unknown types).
    /// </summary>
    public int UnsupportedDatabaseObjectCount { get; set; }

    /// <summary>
    /// Count of project-side objects that were detected but not
    /// compared because their types are not in the supported
    /// whitelist.
    /// </summary>
    public int UnsupportedFileObjectCount { get; set; }

    public Dictionary<string, int> ByType { get; set; } = new();
    public Dictionary<string, int> ByAction { get; set; } = new();
    public Dictionary<string, int> ByDirection { get; set; } = new();
}

public sealed class GetUnsupportedObjectsResponse
{
    public Guid ComparisonId { get; set; }

    public List<UnsupportedObjectResponse> Objects { get; set; } = new();

    public int TotalCount { get; set; }

    public int DatabaseCount { get; set; }

    public int FileCount { get; set; }
}

public sealed class UnsupportedObjectResponse
{
    /// <summary>
    /// "database" when the object came from the database snapshot,
    /// "file" when it came from the project files.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Logical object type (e.g. table, view, login, role, user, unknown).
    /// Uses the same normalized keys as other comparison responses.
    /// </summary>
    public string ObjectType { get; set; } = string.Empty;

    public string? SchemaName { get; set; }

    public string ObjectName { get; set; } = string.Empty;

    public string? FilePath { get; set; }
}

public sealed class GetComparisonDifferencesResponse
{
    public Guid ComparisonId { get; set; }
    public List<ComparisonDifferenceListItemResponse> Differences { get; set; } = new();
    public int TotalCount { get; set; }
}

public sealed class ComparisonDifferenceListItemResponse
{
    public Guid Id { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public string? FilePath { get; set; }
    public string? SuggestedFilePath { get; set; }
    public ChangeDetailsResponse? ChangeDetails { get; set; }
}

public sealed class ChangeDetailsResponse
{
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public List<string> ColumnsChanged { get; set; } = new();
}

public sealed class ComparisonDifferenceDetailResponse
{
    public Guid Id { get; set; }
    public Guid ComparisonId { get; set; }
    public Guid SubscriptionId { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? DatabaseScript { get; set; }
    public string? FileScript { get; set; }
    public string? UnifiedDiff { get; set; }
    public SideBySideDiffResponse? SideBySideDiff { get; set; }
    public List<PropertyDifferenceResponse> PropertyChanges { get; set; } = new();
}

public sealed class SideBySideDiffResponse
{
    public List<DiffLineResponse> Lines { get; set; } = new();
}

public sealed class DiffLineResponse
{
    public int LineNumber { get; set; }
    public string? Left { get; set; }
    public string? Right { get; set; }
    public string Type { get; set; } = string.Empty;
}

public sealed class PropertyDifferenceResponse
{
    public string PropertyName { get; set; } = string.Empty;
    public string? DatabaseValue { get; set; }
    public string? FileValue { get; set; }
}
