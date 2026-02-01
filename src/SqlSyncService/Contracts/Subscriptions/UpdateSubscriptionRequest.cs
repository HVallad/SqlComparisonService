namespace SqlSyncService.Contracts.Subscriptions;

public sealed class UpdateSubscriptionRequest
{
    public string? Name { get; set; }

    public UpdateSubscriptionDatabaseConfig? Database { get; set; }

    public UpdateSubscriptionProjectConfig? Project { get; set; }

    public UpdateSubscriptionOptionsConfig? Options { get; set; }
}

public sealed class UpdateSubscriptionDatabaseConfig
{
    public string? Server { get; set; }

    public string? Database { get; set; }

    public string? AuthType { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool? TrustServerCertificate { get; set; }

    public int? ConnectionTimeoutSeconds { get; set; }
}

public sealed class UpdateSubscriptionProjectConfig
{
    public string? Path { get; set; }

    public string[]? IncludePatterns { get; set; }

    public string[]? ExcludePatterns { get; set; }

    public string? Structure { get; set; }
}

public sealed class UpdateSubscriptionOptionsConfig
{
    public bool? AutoCompare { get; set; }

    public bool? CompareOnFileChange { get; set; }

    public bool? CompareOnDatabaseChange { get; set; }

    public string[]? ObjectTypes { get; set; }

    public bool? IgnoreWhitespace { get; set; }

    public bool? IgnoreComments { get; set; }
}

