namespace SqlSyncService.Contracts.Comparisons;

public sealed class TriggerComparisonRequest
{
    /// <summary>
    /// When true, forces a full snapshot rebuild before comparison.
    /// When false or omitted, an incremental comparison may be used.
    /// </summary>
    public bool? ForceFullComparison { get; set; }

    /// <summary>
    /// Optional allow-list of object types to compare (e.g. "table", "view").
    /// When omitted or empty, all default object types are included.
    /// </summary>
    public string[]? ObjectTypes { get; set; }

    /// <summary>
    /// Optional allow-list of specific object names (schema-qualified).
    /// Currently reserved for future use.
    /// </summary>
    public string[]? ObjectNames { get; set; }
}

