namespace SqlSyncService.Contracts.Subscriptions;

public sealed class SubscriptionPauseResponse
{
    public Guid Id { get; set; }
    public string State { get; set; } = "paused";
    public DateTime PausedAt { get; set; }
}

public sealed class SubscriptionResumeResponse
{
    public Guid Id { get; set; }
    public string State { get; set; } = "active";
    public DateTime ResumedAt { get; set; }
}

