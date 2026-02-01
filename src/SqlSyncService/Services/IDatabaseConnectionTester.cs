using SqlSyncService.Contracts.Connections;

namespace SqlSyncService.Services;

public interface IDatabaseConnectionTester
{
    Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken = default);
}
