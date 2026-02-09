using System;

namespace SqlSyncService.Realtime.Events;

public sealed record ServiceShuttingDownEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int GracePeriodSeconds { get; init; }
}

