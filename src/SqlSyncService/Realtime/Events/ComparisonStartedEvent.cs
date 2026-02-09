using System;

namespace SqlSyncService.Realtime.Events;

public sealed record ComparisonStartedEvent
{
    public Guid SubscriptionId { get; init; }
    public Guid ComparisonId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public int EstimatedObjects { get; init; }
}

