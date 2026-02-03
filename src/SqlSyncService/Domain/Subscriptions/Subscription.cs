using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Subscriptions;

public class Subscription
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DatabaseConnection Database { get; set; } = new();
    public ProjectFolder Project { get; set; } = new();
    public SubscriptionState State { get; set; } = SubscriptionState.Active;
    public ComparisonOptions Options { get; set; } = new();
    public SubscriptionHealth Health { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastComparedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime? ResumedAt { get; set; }

    public bool IsActive() => State == SubscriptionState.Active;

    public bool IsPaused() => State == SubscriptionState.Paused;
}

public enum SubscriptionState
{
    Active,
    Paused,
    Error,
    Comparing,
    Syncing
}

