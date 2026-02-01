namespace SqlSyncService.Contracts.Connections;

public sealed class ConnectionPermissions
{
    public bool CanRead { get; set; }

    public bool CanWrite { get; set; }

    public bool CanExecuteDdl { get; set; }
}
