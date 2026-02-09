using System;

namespace SqlSyncService.Realtime.Events;

public sealed record FileChangedEvent
{
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string ChangeType { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string? OldFilePath { get; init; }
    public string? ObjectName { get; init; }
    public string? ObjectType { get; init; }
}

