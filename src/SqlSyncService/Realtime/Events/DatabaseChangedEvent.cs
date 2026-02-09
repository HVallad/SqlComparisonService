using System;

namespace SqlSyncService.Realtime.Events;

public sealed record DatabaseChangedEvent
{
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string ChangeType { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public string ObjectType { get; init; } = string.Empty;
    public string? ModifiedBy { get; init; }
}

