using System;

namespace SqlSyncService.Realtime.Events;

public sealed record SubscriptionStateChangedEvent
{
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string PreviousState { get; init; } = string.Empty;
    public string NewState { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string TriggeredBy { get; init; } = string.Empty;
}

