using Microsoft.Data.SqlClient;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.DacFx;

public interface IDatabaseModelBuilder
{
    Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a schema snapshot filtered to only include specific object types.
    /// This is significantly faster than extracting the full schema when only
    /// comparing a specific object type.
    /// </summary>
    /// <param name="subscriptionId">The subscription identifier.</param>
    /// <param name="connection">The database connection.</param>
    /// <param name="filterObjectType">Optional object type to filter extraction. If null, extracts all types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, SqlObjectType? filterObjectType, CancellationToken cancellationToken = default);
}

public class DatabaseModelBuilder : IDatabaseModelBuilder
{
    private readonly IDatabaseSchemaReader _schemaReader;

    // Test hook for logins - kept for testing server-level principals
    internal static Func<DatabaseConnection, CancellationToken, Task<IReadOnlyCollection<SchemaObjectSummary>>>? LoadLoginsOverride { get; set; }

    public DatabaseModelBuilder() : this(new DatabaseSchemaReader())
    {
    }

    public DatabaseModelBuilder(IDatabaseSchemaReader schemaReader)
    {
        _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
    }

    public Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        return BuildSnapshotAsync(subscriptionId, connection, filterObjectType: null, cancellationToken);
    }

    public async Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, SqlObjectType? filterObjectType, CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));

        IReadOnlyList<SchemaObjectSummary> objects;

        if (filterObjectType.HasValue)
        {
            // Filtered extraction - only get objects of the specified type
            objects = await _schemaReader.GetObjectsByTypeAsync(connection, filterObjectType.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Full extraction
            objects = await _schemaReader.GetAllObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var objectsList = objects.ToList();
        var hash = ComputeSchemaHash(objectsList);

	        var snapshot = new SchemaSnapshot
	        {
	            SubscriptionId = subscriptionId,
	            CapturedAt = DateTime.UtcNow,
	            DatabaseVersion = connection.Database,
	            // New snapshots are created with the current normalization
	            // pipeline version so that repository logic can avoid
	            // double-normalizing definitions on load.
	            NormalizationVersion = SchemaSnapshot.CurrentNormalizationVersion,
	            Hash = hash,
	            Objects = objectsList
	        };

        // Logins are server-level principals. We load them separately.
        // Skip loading logins if we're filtering for a specific object type
        // that is not Login.
        if (filterObjectType.HasValue && filterObjectType.Value != SqlObjectType.Login)
        {
            // Skip login loading for filtered extractions
        }
        else if (LoadLoginsOverride is not null)
        {
            var loginSummaries = await LoadLoginsOverride(connection, cancellationToken).ConfigureAwait(false);
            if (loginSummaries is not null)
            {
                snapshot.Objects.AddRange(loginSummaries);
            }
        }
        else
        {
            var loginSummaries = await LoadLoginsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (loginSummaries.Count > 0)
            {
                snapshot.Objects.AddRange(loginSummaries);
            }
        }

        return snapshot;
    }

    private static async Task<List<SchemaObjectSummary>> LoadLoginsAsync(DatabaseConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<SchemaObjectSummary>();

        // We use the same connection string as for the dacpac extraction,
        // but only need read access to sys.server_principals. If this fails
        // (no permissions, no server, etc.), we simply return an empty list
        // and skip login comparison.
        try
        {
            await using var sqlConnection = new SqlConnection(connection.ConnectionString);
            await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"
	SELECT name
	FROM sys.server_principals
	WHERE type IN ('S','U','G','X') -- SQL, Windows, group, external logins
	  AND name NOT LIKE '##%';      -- ignore internal system logins
	";

            await using var command = new SqlCommand(sql, sqlConnection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var name = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new SchemaObjectSummary
                {
                    SchemaName = string.Empty,
                    ObjectName = name,
                    ObjectType = SqlObjectType.Login,
                    ModifiedDate = null,
                    DefinitionHash = string.Empty,
                    DefinitionScript = string.Empty
                });
            }
        }
        catch
        {
            // Intentionally swallow all exceptions here. If we cannot query
            // server principals (e.g. insufficient permissions or no server),
            // we simply do not participate in login comparison.
        }

        return results;
    }

    private static string ComputeSchemaHash(IEnumerable<SchemaObjectSummary> objects)
    {
        var combined = string.Join("|", objects
            .OrderBy(o => o.SchemaName)
            .ThenBy(o => o.ObjectName)
            .Select(o => o.DefinitionHash));

        return ComputeSha256(Encoding.UTF8.GetBytes(combined));
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}
