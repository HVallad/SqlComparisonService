namespace SqlSyncService.Contracts.Subscriptions;

public sealed class GetSubscriptionsResponse
{
    public List<SubscriptionListItemResponse> Subscriptions { get; set; } = new();
    public int TotalCount { get; set; }
}

public sealed class SubscriptionListItemResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "active";
    public SubscriptionDatabaseSummaryResponse Database { get; set; } = new();
    public SubscriptionProjectSummaryResponse Project { get; set; } = new();
    public DateTime? LastComparedAt { get; set; }
    public int DifferenceCount { get; set; }
    public SubscriptionHealthSummaryResponse Health { get; set; } = new();
}

public sealed class SubscriptionDatabaseSummaryResponse
{
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class SubscriptionProjectSummaryResponse
{
    public string Path { get; set; } = string.Empty;
    public int SqlFileCount { get; set; }
}

public sealed class SubscriptionHealthSummaryResponse
{
    public string Database { get; set; } = "unknown";
    public string FileSystem { get; set; } = "unknown";
}

public sealed class SubscriptionDetailResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public SubscriptionDatabaseDetailResponse Database { get; set; } = new();
    public SubscriptionProjectDetailResponse Project { get; set; } = new();
    public SubscriptionOptionsResponse Options { get; set; } = new();
    public SubscriptionLastComparisonResponse? LastComparison { get; set; }
    public SubscriptionHealthDetailResponse Health { get; set; } = new();
    public SubscriptionStatisticsResponse Statistics { get; set; } = new();
}

public sealed class SubscriptionDatabaseDetailResponse
{
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string AuthType { get; set; } = "windows";
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class SubscriptionProjectDetailResponse
{
    public string Path { get; set; } = string.Empty;
    public string[] IncludePatterns { get; set; } = Array.Empty<string>();
    public string[] ExcludePatterns { get; set; } = Array.Empty<string>();
    public string Structure { get; set; } = "by-type";
    public int SqlFileCount { get; set; }
}

public sealed class SubscriptionOptionsResponse
{
    public bool AutoCompare { get; set; }
    public bool CompareOnFileChange { get; set; }
    public bool CompareOnDatabaseChange { get; set; }
    public string[] ObjectTypes { get; set; } = Array.Empty<string>();
    public bool IgnoreWhitespace { get; set; }
    public bool IgnoreComments { get; set; }
}

public sealed class SubscriptionLastComparisonResponse
{
    public Guid Id { get; set; }
    public DateTime ComparedAt { get; set; }
    public string Duration { get; set; } = string.Empty;
    public int DifferenceCount { get; set; }
}

public sealed class SubscriptionHealthDetailResponse
{
    public SubscriptionHealthCheckResponse Database { get; set; } = new();
    public SubscriptionHealthCheckResponse FileSystem { get; set; } = new();
}

public sealed class SubscriptionHealthCheckResponse
{
    public string Status { get; set; } = "unknown";
    public DateTime? LastChecked { get; set; }
}

public sealed class SubscriptionStatisticsResponse
{
    public int TotalComparisons { get; set; }
}

