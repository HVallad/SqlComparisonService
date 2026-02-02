using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Caching;

public class FileModelCache
{
    public Guid SubscriptionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public Dictionary<string, FileObjectEntry> FileEntries { get; set; } = new();
}

public class FileObjectEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public string Content { get; set; } = string.Empty;
}

