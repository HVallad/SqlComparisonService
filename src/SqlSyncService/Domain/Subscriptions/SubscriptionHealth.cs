namespace SqlSyncService.Domain.Subscriptions;

/// <summary>
/// Represents the health status of a subscription.
/// </summary>
public class SubscriptionHealth
{
    /// <summary>
    /// Whether the database is connectable.
    /// </summary>
    public bool DatabaseConnectable { get; set; }

    /// <summary>
    /// Whether the project folder is accessible.
    /// </summary>
    public bool FolderAccessible { get; set; }

    /// <summary>
    /// Whether at least one SQL file exists in the project folder.
    /// </summary>
    public bool SqlFilesPresent { get; set; }

    /// <summary>
    /// When the health check was last performed.
    /// </summary>
    public DateTime LastCheckedAt { get; set; }

    /// <summary>
    /// The last error message encountered, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The overall health status.
    /// </summary>
    public HealthStatus OverallStatus { get; set; } = HealthStatus.Unknown;
}

/// <summary>
/// Health status enumeration.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// Health status is unknown (not yet checked).
    /// </summary>
    Unknown,

    /// <summary>
    /// All health checks pass.
    /// </summary>
    Healthy,

    /// <summary>
    /// Some health checks fail but core functionality may still work.
    /// </summary>
    Degraded,

    /// <summary>
    /// Critical health checks fail, subscription cannot function.
    /// </summary>
    Unhealthy
}

