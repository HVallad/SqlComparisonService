using System.ComponentModel.DataAnnotations;

namespace SqlSyncService.Contracts.Subscriptions;

public sealed class CreateSubscriptionRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public CreateSubscriptionDatabaseConfig Database { get; set; } = new();

    [Required]
    public CreateSubscriptionProjectConfig Project { get; set; } = new();

    [Required]
    public CreateSubscriptionOptionsConfig Options { get; set; } = new();
}

public sealed class CreateSubscriptionDatabaseConfig
{
    [Required]
    public string Server { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = string.Empty;

    [Required]
    public string AuthType { get; set; } = "windows";

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool TrustServerCertificate { get; set; }

    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

public sealed class CreateSubscriptionProjectConfig
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public string[]? IncludePatterns { get; set; }

    public string[]? ExcludePatterns { get; set; }

    public string Structure { get; set; } = "by-type";
}

public sealed class CreateSubscriptionOptionsConfig
{
    public bool AutoCompare { get; set; } = true;

    public bool CompareOnFileChange { get; set; } = true;

    public bool CompareOnDatabaseChange { get; set; } = true;

    public string[]? ObjectTypes { get; set; }

    public bool IgnoreWhitespace { get; set; } = true;

    public bool IgnoreComments { get; set; } = false;
}

