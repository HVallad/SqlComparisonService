using SqlSyncService.Contracts.Common;

namespace SqlSyncService.Contracts.Connections;

public sealed class TestConnectionResponse
{
    public bool Success { get; set; }

    public string? ServerVersion { get; set; }

    public string? ServerEdition { get; set; }

    public bool? DatabaseExists { get; set; }

    public ObjectCounts? ObjectCounts { get; set; }

    public ConnectionPermissions? Permissions { get; set; }

    public ErrorDetail? Error { get; set; }
}
