namespace SqlSyncService.Contracts.Common;

public sealed class ObjectCounts
{
    public int Tables { get; set; }

    public int Views { get; set; }

    public int StoredProcedures { get; set; }

    public int Functions { get; set; }
}
