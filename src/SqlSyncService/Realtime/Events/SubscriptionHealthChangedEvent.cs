using System;
using System.Collections.Generic;

namespace SqlSyncService.Realtime.Events;

public sealed record SubscriptionHealthChangedEvent
{
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string PreviousHealth { get; init; } = string.Empty;
    public string NewHealth { get; init; } = string.Empty;
    public IReadOnlyList<SubscriptionHealthIssue> Issues { get; init; } = Array.Empty<SubscriptionHealthIssue>();
}

public sealed record SubscriptionHealthIssue
{
    public string Type { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset Since { get; init; }
}

