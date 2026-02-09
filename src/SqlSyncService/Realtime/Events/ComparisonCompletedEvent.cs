using System;

namespace SqlSyncService.Realtime.Events;

public sealed record ComparisonCompletedEvent
{
    public Guid SubscriptionId { get; init; }
    public Guid ComparisonId { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string Duration { get; init; } = string.Empty;
    public int DifferenceCount { get; init; }
    public string Status { get; init; } = string.Empty;
}

