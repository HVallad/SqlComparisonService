using System;

namespace SqlSyncService.Realtime.Events;

public sealed record DifferencesDetectedEvent
{
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Guid ComparisonId { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public int DifferenceCount { get; init; }
    public DifferencesSummary Summary { get; init; } = new();
}

public sealed record DifferencesSummary
{
    public DifferencesByActionSummary ByAction { get; init; } = new();
}

public sealed record DifferencesByActionSummary
{
    public int Add { get; init; }
    public int Delete { get; init; }
    public int Change { get; init; }
}

