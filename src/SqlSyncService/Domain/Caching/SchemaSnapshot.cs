using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Caching;

public class SchemaSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public string DatabaseVersion { get; set; } = string.Empty;
	    /// <summary>
	    /// Tracks which normalization pipeline version has been applied to this
	    /// snapshot. Version 0 means "unknown/legacy" (no migration applied).
	    /// </summary>
	    public int NormalizationVersion { get; set; }
    public string Hash { get; set; } = string.Empty;
    public List<SchemaObjectSummary> Objects { get; set; } = new();

	    /// <summary>
	    /// Current normalization pipeline version. When new normalization rules
	    /// are introduced that require migrating persisted snapshots, this value
	    /// should be incremented and migration logic updated accordingly.
	    /// </summary>
	    public const int CurrentNormalizationVersion = 1;
}

public class SchemaObjectSummary
{
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
    public string DefinitionScript { get; set; } = string.Empty;
}

