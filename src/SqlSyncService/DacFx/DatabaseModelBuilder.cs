using Microsoft.SqlServer.Dac;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Subscriptions;
using System.Security.Cryptography;

namespace SqlSyncService.DacFx;

public interface IDatabaseModelBuilder
{
    Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default);
}

public class DatabaseModelBuilder : IDatabaseModelBuilder
{
    public async Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));

        var dacpacBytes = await ExtractDacpacBytesAsync(connection, cancellationToken).ConfigureAwait(false);
        var hash = ComputeSha256(dacpacBytes);

        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            DatabaseVersion = connection.Database,
            DacpacBytes = dacpacBytes,
            Hash = hash
        };

        return snapshot;
    }

    private static async Task<byte[]> ExtractDacpacBytesAsync(DatabaseConnection connection, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var dacServices = new DacServices(connection.ConnectionString);

        var applicationName = "SqlSyncService";
        var version = new Version(1, 0, 0, 0);

        await Task.Run(
            () => dacServices.Extract(
                ms,
                connection.Database,
                applicationName,
                version,
                null,
                null,
                new DacExtractOptions(),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return ms.ToArray();
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash);
    }
}

