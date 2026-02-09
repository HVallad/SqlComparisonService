using System;
using System.Collections.Generic;

namespace SqlSyncService.Realtime.Events;

public sealed record ServiceReconnectedEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset? DisconnectedAt { get; init; }
    public int MissedEvents { get; init; }
    public IReadOnlyList<Guid> ActiveSubscriptions { get; init; } = Array.Empty<Guid>();
}

