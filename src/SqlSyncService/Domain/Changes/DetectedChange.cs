using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Changes;

public class DetectedChange
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public ChangeSource Source { get; set; }
    public ChangeType Type { get; set; }
    public string ObjectIdentifier { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public bool IsProcessed { get; set; }
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// The type of SQL object that changed. Only populated for database changes
    /// where the specific object type is known.
    /// </summary>
    public SqlObjectType? ObjectType { get; set; }
}

public enum ChangeSource
{
    Database,
    FileSystem
}

public enum ChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}

public class PendingChangeBatch
{
    public Guid SubscriptionId { get; set; }
    public List<DetectedChange> Changes { get; set; } = new();
    public DateTime BatchStartedAt { get; set; }
    public DateTime? BatchCompletedAt { get; set; }
}

