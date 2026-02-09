using System;

namespace SqlSyncService.Realtime.Events;

public sealed record ComparisonProgressEvent
{
    public Guid SubscriptionId { get; init; }
    public Guid ComparisonId { get; init; }
    public string Phase { get; init; } = string.Empty;
    public int ObjectsProcessed { get; init; }
    public int TotalObjects { get; init; }
    public int PercentComplete { get; init; }
    public string? CurrentObject { get; init; }
}

