using System;

namespace SqlSyncService.Realtime.Events;

public sealed record SubscriptionDeletedEvent
{
    public Guid SubscriptionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset DeletedAt { get; init; }
}

