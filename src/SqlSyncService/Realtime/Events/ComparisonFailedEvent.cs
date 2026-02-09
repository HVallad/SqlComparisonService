using System;

namespace SqlSyncService.Realtime.Events;

public sealed record ComparisonFailedEvent
{
    public Guid SubscriptionId { get; init; }
    public Guid ComparisonId { get; init; }
    public DateTimeOffset FailedAt { get; init; }
    public ComparisonError Error { get; init; } = new();
}

public sealed record ComparisonError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

