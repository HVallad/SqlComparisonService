namespace SqlSyncService.Domain.Comparisons;

public class ComparisonOptions
{
    public bool IncludeTables { get; set; } = true;
    public bool IncludeViews { get; set; } = true;
    public bool IncludeStoredProcedures { get; set; } = true;
    public bool IncludeFunctions { get; set; } = true;
    public bool IncludeTriggers { get; set; } = true;

    public bool IgnoreWhitespace { get; set; } = true;
    public bool IgnoreComments { get; set; } = false;
    public bool IgnoreColumnOrder { get; set; } = true;
    public bool IgnoreTableOptions { get; set; } = false;

    public bool IncludeExtendedProperties { get; set; } = false;
    public bool IncludePermissions { get; set; } = false;
}

