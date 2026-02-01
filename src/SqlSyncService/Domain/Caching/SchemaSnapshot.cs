using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Caching;

public class SchemaSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public string DatabaseVersion { get; set; } = string.Empty;
    public byte[] DacpacBytes { get; set; } = Array.Empty<byte>();
    public string Hash { get; set; } = string.Empty;
    public List<SchemaObjectSummary> Objects { get; set; } = new();
}

public class SchemaObjectSummary
{
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
}

