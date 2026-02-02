using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.DacFx;

public interface IDatabaseModelBuilder
{
    Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default);
}

public class DatabaseModelBuilder : IDatabaseModelBuilder
{
	    // Test hooks: these allow unit tests to override the expensive/external
	    // DacFx calls so BuildSnapshotAsync can be exercised without a real
	    // database or dacpac. They are internal and only visible to tests via
	    // InternalsVisibleTo.
	    internal static Func<DatabaseConnection, CancellationToken, Task<byte[]>>? ExtractDacpacOverride { get; set; }
	    internal static Action<SchemaSnapshot>? PopulateObjectsOverride { get; set; }
		    internal static Func<DatabaseConnection, CancellationToken, Task<IReadOnlyCollection<SchemaObjectSummary>>>? LoadLoginsOverride { get; set; }

    public async Task<SchemaSnapshot> BuildSnapshotAsync(Guid subscriptionId, DatabaseConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));

	        var dacpacBytes = ExtractDacpacOverride is not null
	            ? await ExtractDacpacOverride(connection, cancellationToken).ConfigureAwait(false)
	            : await ExtractDacpacBytesAsync(connection, cancellationToken).ConfigureAwait(false);
        var hash = ComputeSha256(dacpacBytes);

	        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            DatabaseVersion = connection.Database,
	            DacpacBytes = dacpacBytes,
	            Hash = hash
        };

	        	if (PopulateObjectsOverride is not null)
	        	{
	        		PopulateObjectsOverride(snapshot);
	        	}
	        	else
	        	{
	        		PopulateObjectsFromDacpac(snapshot);
	        	}

		        // Logins are server-level principals, not part of the dacpac. We
		        // load them separately using the live connection. Tests can
		        // override this to avoid hitting a real SQL Server instance.
		        if (LoadLoginsOverride is not null)
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

	    internal static void PopulateObjectsFromDacpac(SchemaSnapshot snapshot)
	    {
	        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
	        if (snapshot.DacpacBytes is null || snapshot.DacpacBytes.Length == 0)
	        {
	            return;
	        }

	        using var ms = new MemoryStream(snapshot.DacpacBytes, writable: false);
	        var loadOptions = new ModelLoadOptions();
	        using var model = TSqlModel.LoadFromDacpac(ms, loadOptions);

	        var objects = new List<SchemaObjectSummary>();

	        void AddObjects(IEnumerable<TSqlObject> tsqlObjects, SqlObjectType type)
	        {
	            foreach (var obj in tsqlObjects)
	            {
	                var summary = BuildSchemaObjectSummary(obj, type);
	                if (summary is not null)
	                {
	                    objects.Add(summary);
	                }
	            }
	        }

		        // Tables, views, procedures, and functions
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table), SqlObjectType.Table);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View), SqlObjectType.View);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Procedure), SqlObjectType.StoredProcedure);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ScalarFunction), SqlObjectType.ScalarFunction);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.TableValuedFunction), SqlObjectType.TableValuedFunction);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DmlTrigger), SqlObjectType.Trigger);

		        // Security principals (database-level only). These rely on DacFx model support
		        // for users and roles; server-level principals such as logins are not represented
		        // in a database dacpac and therefore are not included here.
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.User), SqlObjectType.User);
		        AddObjects(model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Role), SqlObjectType.Role);

		        // Additional types can be added here later (indexes, constraints, types, schemas, synonyms, etc.)

	        snapshot.Objects = objects;
	    }

	    private static SchemaObjectSummary? BuildSchemaObjectSummary(TSqlObject obj, SqlObjectType type)
	    {
	        if (obj is null)
	        {
	            return null;
	        }

	        var (schemaName, objectName) = SplitNameParts(obj.Name.Parts);
		
		    string script;
		    try
		    {
		        script = obj.GetScript();
		    }
		    catch
		    {
		        script = string.Empty;
		    }
		
		    var normalizedScript = SqlScriptNormalizer.Normalize(script);
		    var scriptBytes = Encoding.UTF8.GetBytes(normalizedScript);
		    var definitionHash = ComputeSha256(scriptBytes);
		
	        return new SchemaObjectSummary
	        {
	            SchemaName = schemaName,
	            ObjectName = objectName,
	            ObjectType = type,
	            ModifiedDate = null,
		        DefinitionHash = definitionHash,
		        DefinitionScript = normalizedScript
	        };
	    }

	    internal static (string SchemaName, string ObjectName) SplitNameParts(IList<string> parts)
	    {
	        if (parts is null || parts.Count == 0)
	        {
	            return (string.Empty, string.Empty);
	        }

	        if (parts.Count == 1)
	        {
	            return ("dbo", parts[0]);
	        }

	        // Use the last two parts as schema and object name. For names like [db].[schema].[name], this
	        // correctly picks the schema and object name.
	        var objectName = parts[parts.Count - 1];
	        var schemaName = parts[parts.Count - 2];
	        return (schemaName, objectName);
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

