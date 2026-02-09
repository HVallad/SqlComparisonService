using System;

namespace SqlSyncService.Realtime.Events;

public sealed record SubscriptionCreatedEvent
{
    public Guid SubscriptionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public SubscriptionCreatedDatabaseInfo Database { get; init; } = new();
    public SubscriptionCreatedProjectInfo Project { get; init; } = new();
}

public sealed record SubscriptionCreatedDatabaseInfo
{
    public string Server { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
}

public sealed record SubscriptionCreatedProjectInfo
{
    public string ProjectPath { get; init; } = string.Empty;
}

