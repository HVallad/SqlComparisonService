using Microsoft.Data.SqlClient;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.DacFx;

/// <summary>
/// Interface for reading schema objects directly from SQL Server system views.
/// </summary>
public interface IDatabaseSchemaReader
{
    /// <summary>
    /// Gets all supported schema objects from the database.
    /// </summary>
    Task<IReadOnlyList<SchemaObjectSummary>> GetAllObjectsAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific object by name and type.
    /// </summary>
    Task<SchemaObjectSummary?> GetObjectAsync(
        DatabaseConnection connection,
        string schemaName,
        string objectName,
        SqlObjectType objectType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all objects of a specific type from the database.
    /// </summary>
    Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsByTypeAsync(
        DatabaseConnection connection,
        SqlObjectType objectType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple specific objects in batched queries (one query per object type).
    /// This is more efficient than calling GetObjectAsync multiple times.
    /// </summary>
    /// <param name="connection">The database connection information.</param>
    /// <param name="objectsToQuery">The objects to query, identified by schema, name, and type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The objects that were found. Objects not in the database are not returned.</returns>
    Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsAsync(
        DatabaseConnection connection,
        IEnumerable<ObjectIdentifier> objectsToQuery,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads schema object definitions directly from SQL Server system views.
/// This replaces the DacFx-based extraction for significantly faster performance.
/// </summary>
public class DatabaseSchemaReader : IDatabaseSchemaReader
{
    // SQL Server object type codes to SqlObjectType mapping
    private static readonly Dictionary<string, SqlObjectType> SqlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "U", SqlObjectType.Table },
        { "V", SqlObjectType.View },
        { "P", SqlObjectType.StoredProcedure },
        { "PC", SqlObjectType.StoredProcedure }, // CLR stored procedure
        { "FN", SqlObjectType.ScalarFunction },
        { "FS", SqlObjectType.ScalarFunction }, // CLR scalar function
        { "IF", SqlObjectType.InlineTableValuedFunction },
        { "TF", SqlObjectType.TableValuedFunction },
        { "FT", SqlObjectType.TableValuedFunction }, // CLR table-valued function
        { "TR", SqlObjectType.Trigger }
    };

    // Query for programmable objects (procedures, functions, views, triggers)
    // This includes both T-SQL and CLR-based objects
    private const string ProgrammableObjectsQuery = @"
        SELECT
            SCHEMA_NAME(o.schema_id) AS SchemaName,
            o.name AS ObjectName,
            o.type AS ObjectType,
            o.modify_date AS ModifyDate,
            sm.definition COLLATE DATABASE_DEFAULT AS Definition
        FROM sys.objects o
        JOIN sys.sql_modules sm ON o.object_id = sm.object_id
        WHERE o.type IN ('P', 'FN', 'TF', 'IF', 'V', 'TR')
          AND o.is_ms_shipped = 0
        UNION ALL
        SELECT
            SCHEMA_NAME(o.schema_id) AS SchemaName,
            o.name AS ObjectName,
            o.type AS ObjectType,
            o.modify_date AS ModifyDate,
            'EXTERNAL NAME [' + a.name + '].[' + am.assembly_class + '].[' + ISNULL(am.assembly_method, '') + ']' COLLATE DATABASE_DEFAULT AS Definition
        FROM sys.objects o
        JOIN sys.assembly_modules am ON o.object_id = am.object_id
        JOIN sys.assemblies a ON am.assembly_id = a.assembly_id
        WHERE o.type IN ('PC', 'FS', 'FT')
          AND o.is_ms_shipped = 0
        ORDER BY ObjectType, SchemaName, ObjectName";

    // Query for a specific programmable object (both T-SQL and CLR-based)
    private const string SingleProgrammableObjectQuery = @"
        SELECT
            SCHEMA_NAME(o.schema_id) AS SchemaName,
            o.name AS ObjectName,
            o.type AS ObjectType,
            o.modify_date AS ModifyDate,
            sm.definition COLLATE DATABASE_DEFAULT AS Definition
        FROM sys.objects o
        JOIN sys.sql_modules sm ON o.object_id = sm.object_id
        WHERE o.name = @objectName
          AND SCHEMA_NAME(o.schema_id) = @schemaName
          AND o.is_ms_shipped = 0
        UNION ALL
        SELECT
            SCHEMA_NAME(o.schema_id) AS SchemaName,
            o.name AS ObjectName,
            o.type AS ObjectType,
            o.modify_date AS ModifyDate,
            'EXTERNAL NAME [' + a.name + '].[' + am.assembly_class + '].[' + ISNULL(am.assembly_method, '') + ']' COLLATE DATABASE_DEFAULT AS Definition
        FROM sys.objects o
        JOIN sys.assembly_modules am ON o.object_id = am.object_id
        JOIN sys.assemblies a ON am.assembly_id = a.assembly_id
        WHERE o.name = @objectName
          AND SCHEMA_NAME(o.schema_id) = @schemaName
          AND o.is_ms_shipped = 0";

    // Query for all tables
    private const string TablesQuery = @"
        SELECT 
            SCHEMA_NAME(t.schema_id) AS SchemaName,
            t.name AS TableName,
            t.modify_date AS ModifyDate
        FROM sys.tables t
        WHERE t.is_ms_shipped = 0
        ORDER BY SCHEMA_NAME(t.schema_id), t.name";

    // Query for users
    private const string UsersQuery = @"
        SELECT 
            name AS UserName,
            type_desc AS TypeDescription,
            default_schema_name AS DefaultSchema
        FROM sys.database_principals
        WHERE type IN ('S', 'U', 'G', 'E', 'X')
          AND is_fixed_role = 0
          AND name NOT IN ('dbo', 'guest', 'INFORMATION_SCHEMA', 'sys', 'public')
        ORDER BY name";

    // Query for roles
    private const string RolesQuery = @"
        SELECT 
            name AS RoleName,
            type_desc AS TypeDescription
        FROM sys.database_principals
        WHERE type = 'R'
          AND is_fixed_role = 0
          AND name NOT IN ('public')
        ORDER BY name";

    // Query for logins (server-level)
    private const string LoginsQuery = @"
        SELECT name
        FROM sys.server_principals
        WHERE type IN ('S','U','G','X')
          AND name NOT LIKE '##%'
        ORDER BY name";

    public async Task<IReadOnlyList<SchemaObjectSummary>> GetAllObjectsAsync(
        DatabaseConnection connection,
        CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));

        var results = new List<SchemaObjectSummary>();

        await using var sqlConnection = new SqlConnection(connection.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Get programmable objects (procedures, functions, views, triggers)
        results.AddRange(await GetProgrammableObjectsAsync(sqlConnection, cancellationToken).ConfigureAwait(false));

        // Get tables
        results.AddRange(await GetTablesAsync(sqlConnection, cancellationToken).ConfigureAwait(false));

        // Get indexes
        results.AddRange(await GetIndexesAsync(sqlConnection, cancellationToken).ConfigureAwait(false));

        // Get users
        results.AddRange(await GetUsersAsync(sqlConnection, cancellationToken).ConfigureAwait(false));

        // Get roles
        results.AddRange(await GetRolesAsync(sqlConnection, cancellationToken).ConfigureAwait(false));

        return results;
    }

    public async Task<SchemaObjectSummary?> GetObjectAsync(
        DatabaseConnection connection,
        string schemaName,
        string objectName,
        SqlObjectType objectType,
        CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));

        await using var sqlConnection = new SqlConnection(connection.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return objectType switch
        {
            SqlObjectType.StoredProcedure or SqlObjectType.ScalarFunction or
            SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction or
            SqlObjectType.View or SqlObjectType.Trigger =>
                await GetSingleProgrammableObjectAsync(sqlConnection, schemaName, objectName, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Table =>
                await GetSingleTableAsync(sqlConnection, schemaName, objectName, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Index =>
                await GetSingleIndexAsync(sqlConnection, schemaName, objectName, cancellationToken).ConfigureAwait(false),
            SqlObjectType.User =>
                await GetSingleUserAsync(sqlConnection, objectName, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Role =>
                await GetSingleRoleAsync(sqlConnection, objectName, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    public async Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsByTypeAsync(
        DatabaseConnection connection,
        SqlObjectType objectType,
        CancellationToken cancellationToken = default)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));

        await using var sqlConnection = new SqlConnection(connection.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return objectType switch
        {
            SqlObjectType.StoredProcedure or SqlObjectType.ScalarFunction or
            SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction or
            SqlObjectType.View or SqlObjectType.Trigger =>
                await GetProgrammableObjectsByTypeAsync(sqlConnection, objectType, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Table =>
                await GetTablesAsync(sqlConnection, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Index =>
                await GetIndexesAsync(sqlConnection, cancellationToken).ConfigureAwait(false),
            SqlObjectType.User =>
                await GetUsersAsync(sqlConnection, cancellationToken).ConfigureAwait(false),
            SqlObjectType.Role =>
                await GetRolesAsync(sqlConnection, cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<SchemaObjectSummary>()
        };
    }

    public async Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsAsync(
        DatabaseConnection connection,
        IEnumerable<ObjectIdentifier> objectsToQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(objectsToQuery);

        var objectList = objectsToQuery.ToList();
        if (objectList.Count == 0)
        {
            return Array.Empty<SchemaObjectSummary>();
        }

        var results = new List<SchemaObjectSummary>();

        await using var sqlConnection = new SqlConnection(connection.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Group objects by type for efficient batching
        var groupedByType = objectList.GroupBy(o => o.ObjectType);

        foreach (var group in groupedByType)
        {
            var objectType = group.Key;
            var objectsInGroup = group.ToList();

            var groupResults = objectType switch
            {
                SqlObjectType.StoredProcedure or SqlObjectType.ScalarFunction or
                SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction or
                SqlObjectType.View or SqlObjectType.Trigger =>
                    await GetBatchedProgrammableObjectsAsync(sqlConnection, objectsInGroup, cancellationToken).ConfigureAwait(false),
                SqlObjectType.Table =>
                    await GetBatchedTablesAsync(sqlConnection, objectsInGroup, cancellationToken).ConfigureAwait(false),
                SqlObjectType.Index =>
                    await GetBatchedIndexesAsync(sqlConnection, objectsInGroup, cancellationToken).ConfigureAwait(false),
                SqlObjectType.User =>
                    await GetBatchedUsersAsync(sqlConnection, objectsInGroup, cancellationToken).ConfigureAwait(false),
                SqlObjectType.Role =>
                    await GetBatchedRolesAsync(sqlConnection, objectsInGroup, cancellationToken).ConfigureAwait(false),
                _ => new List<SchemaObjectSummary>()
            };

            results.AddRange(groupResults);
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetBatchedProgrammableObjectsAsync(
        SqlConnection connection,
        IReadOnlyList<ObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return new List<SchemaObjectSummary>();
        }

        // Build WHERE clause with parameterized OR conditions
        var conditions = new List<string>();
        var command = new SqlCommand { Connection = connection };

        for (var i = 0; i < objects.Count; i++)
        {
            conditions.Add($"(SCHEMA_NAME(o.schema_id) = @schema{i} AND o.name = @name{i})");
            command.Parameters.AddWithValue($"@schema{i}", objects[i].SchemaName);
            command.Parameters.AddWithValue($"@name{i}", objects[i].ObjectName);
        }

        var query = $@"
            SELECT
                SCHEMA_NAME(o.schema_id) AS SchemaName,
                o.name AS ObjectName,
                o.type AS ObjectType,
                o.modify_date AS ModifyDate,
                sm.definition COLLATE DATABASE_DEFAULT AS Definition
            FROM sys.objects o
            JOIN sys.sql_modules sm ON o.object_id = sm.object_id
            WHERE o.is_ms_shipped = 0
              AND ({string.Join(" OR ", conditions)})
            UNION ALL
            SELECT
                SCHEMA_NAME(o.schema_id) AS SchemaName,
                o.name AS ObjectName,
                o.type AS ObjectType,
                o.modify_date AS ModifyDate,
                'EXTERNAL NAME [' + a.name + '].[' + am.assembly_class + '].[' + ISNULL(am.assembly_method, '') + ']' COLLATE DATABASE_DEFAULT AS Definition
            FROM sys.objects o
            JOIN sys.assembly_modules am ON o.object_id = am.object_id
            JOIN sys.assemblies a ON am.assembly_id = a.assembly_id
            WHERE o.is_ms_shipped = 0
              AND ({string.Join(" OR ", conditions)})
            ORDER BY ObjectType, SchemaName, ObjectName";

        command.CommandText = query;

        var results = new List<SchemaObjectSummary>();

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Trim object names to match SQL Server's lenient name resolution behavior
                // (SQL Server ignores trailing spaces when resolving object names)
                var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0).Trim();
                var objectName = reader.GetString(1).Trim();
                var sqlType = reader.GetString(2).Trim();
                var modifyDate = reader.GetDateTime(3);
                var definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                if (SqlTypeMap.TryGetValue(sqlType, out var objectType))
                {
                    var summary = BuildProgrammableObjectSummary(schemaName, objectName, objectType, modifyDate, definition);
                    results.Add(summary);
                }
            }
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetBatchedTablesAsync(
        SqlConnection connection,
        IReadOnlyList<ObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return new List<SchemaObjectSummary>();
        }

        // Build WHERE clause with parameterized OR conditions
        var conditions = new List<string>();
        var command = new SqlCommand { Connection = connection };

        for (var i = 0; i < objects.Count; i++)
        {
            conditions.Add($"(SCHEMA_NAME(t.schema_id) = @schema{i} AND t.name = @name{i})");
            command.Parameters.AddWithValue($"@schema{i}", objects[i].SchemaName);
            command.Parameters.AddWithValue($"@name{i}", objects[i].ObjectName);
        }

        var query = $@"
            SELECT
                SCHEMA_NAME(t.schema_id) AS SchemaName,
                t.name AS TableName,
                t.modify_date AS ModifyDate
            FROM sys.tables t
            WHERE t.is_ms_shipped = 0
              AND ({string.Join(" OR ", conditions)})
            ORDER BY SCHEMA_NAME(t.schema_id), t.name";

        command.CommandText = query;

        var tables = new List<(string SchemaName, string TableName, DateTime ModifyDate)>();

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Trim names to match SQL Server's lenient name resolution behavior
                var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0).Trim();
                var tableName = reader.GetString(1).Trim();
                var modifyDate = reader.GetDateTime(2);
                tables.Add((schemaName, tableName, modifyDate));
            }
        }

        var results = new List<SchemaObjectSummary>();
        foreach (var (schemaName, tableName, modifyDate) in tables)
        {
            var definition = await TableScriptBuilder.BuildCreateTableScriptAsync(
                connection, schemaName, tableName, cancellationToken).ConfigureAwait(false);

            var summary = BuildTableSummary(schemaName, tableName, modifyDate, definition);
            results.Add(summary);
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetBatchedIndexesAsync(
        SqlConnection connection,
        IReadOnlyList<ObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return new List<SchemaObjectSummary>();
        }

        var results = new List<SchemaObjectSummary>();

        // Index identifiers are in format "TableName.IndexName"
        foreach (var obj in objects)
        {
            var parts = obj.ObjectName.Split('.');
            if (parts.Length != 2)
            {
                continue;
            }

            var tableName = parts[0];
            var indexName = parts[1];

            var index = await IndexScriptBuilder.GetSingleIndexAsync(
                connection, obj.SchemaName, tableName, indexName, cancellationToken).ConfigureAwait(false);

            if (index != null)
            {
                results.Add(index);
            }
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetBatchedUsersAsync(
        SqlConnection connection,
        IReadOnlyList<ObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return new List<SchemaObjectSummary>();
        }

        // Build WHERE clause with parameterized OR conditions
        var conditions = new List<string>();
        var command = new SqlCommand { Connection = connection };

        for (var i = 0; i < objects.Count; i++)
        {
            conditions.Add($"name = @name{i}");
            command.Parameters.AddWithValue($"@name{i}", objects[i].ObjectName);
        }

        var query = $@"
            SELECT
                name AS UserName,
                type_desc AS TypeDescription,
                default_schema_name AS DefaultSchema
            FROM sys.database_principals
            WHERE type IN ('S', 'U', 'G', 'E', 'X')
              AND is_fixed_role = 0
              AND ({string.Join(" OR ", conditions)})
            ORDER BY name";

        command.CommandText = query;

        var results = new List<SchemaObjectSummary>();

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var userName = reader.GetString(0).Trim();
                var defaultSchema = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();

                var definition = BuildUserScript(userName, defaultSchema);
                var summary = BuildSecurityPrincipalSummary(string.Empty, userName, SqlObjectType.User, definition);
                results.Add(summary);
            }
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetBatchedRolesAsync(
        SqlConnection connection,
        IReadOnlyList<ObjectIdentifier> objects,
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return new List<SchemaObjectSummary>();
        }

        // Build WHERE clause with parameterized OR conditions
        var conditions = new List<string>();
        var command = new SqlCommand { Connection = connection };

        for (var i = 0; i < objects.Count; i++)
        {
            conditions.Add($"name = @name{i}");
            command.Parameters.AddWithValue($"@name{i}", objects[i].ObjectName);
        }

        var query = $@"
            SELECT name AS RoleName
            FROM sys.database_principals
            WHERE type = 'R'
              AND is_fixed_role = 0
              AND ({string.Join(" OR ", conditions)})
            ORDER BY name";

        command.CommandText = query;

        var results = new List<SchemaObjectSummary>();

        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var roleName = reader.GetString(0).Trim();
                var definition = $"CREATE ROLE [{roleName}]";
                var summary = BuildSecurityPrincipalSummary(string.Empty, roleName, SqlObjectType.Role, definition);
                results.Add(summary);
            }
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetProgrammableObjectsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var results = new List<SchemaObjectSummary>();

        await using var command = new SqlCommand(ProgrammableObjectsQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Trim names to match SQL Server's lenient name resolution behavior
            var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0).Trim();
            var objectName = reader.GetString(1).Trim();
            var sqlType = reader.GetString(2).Trim();
            var modifyDate = reader.GetDateTime(3);
            var definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            if (SqlTypeMap.TryGetValue(sqlType, out var objectType))
            {
                var summary = BuildProgrammableObjectSummary(schemaName, objectName, objectType, modifyDate, definition);
                results.Add(summary);
            }
        }

        return results;
    }

    private async Task<List<SchemaObjectSummary>> GetProgrammableObjectsByTypeAsync(
        SqlConnection connection,
        SqlObjectType filterType,
        CancellationToken cancellationToken)
    {
        var sqlTypeCode = GetSqlTypeCode(filterType);
        if (sqlTypeCode is null) return new List<SchemaObjectSummary>();

        var clrTypeCode = GetClrTypeCode(filterType);

        var query = @"
            SELECT
                SCHEMA_NAME(o.schema_id) AS SchemaName,
                o.name AS ObjectName,
                o.type AS ObjectType,
                o.modify_date AS ModifyDate,
                sm.definition COLLATE DATABASE_DEFAULT AS Definition
            FROM sys.objects o
            JOIN sys.sql_modules sm ON o.object_id = sm.object_id
            WHERE o.type = @objectType
              AND o.is_ms_shipped = 0
            UNION ALL
            SELECT
                SCHEMA_NAME(o.schema_id) AS SchemaName,
                o.name AS ObjectName,
                o.type AS ObjectType,
                o.modify_date AS ModifyDate,
                'EXTERNAL NAME [' + a.name + '].[' + am.assembly_class + '].[' + ISNULL(am.assembly_method, '') + ']' COLLATE DATABASE_DEFAULT AS Definition
            FROM sys.objects o
            JOIN sys.assembly_modules am ON o.object_id = am.object_id
            JOIN sys.assemblies a ON am.assembly_id = a.assembly_id
            WHERE o.type = @clrObjectType
              AND o.is_ms_shipped = 0
            ORDER BY SchemaName, ObjectName";

        var results = new List<SchemaObjectSummary>();

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@objectType", sqlTypeCode);
        command.Parameters.AddWithValue("@clrObjectType", clrTypeCode ?? string.Empty);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Trim names to match SQL Server's lenient name resolution behavior
            var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0).Trim();
            var objectName = reader.GetString(1).Trim();
            var modifyDate = reader.GetDateTime(3);
            var definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            var summary = BuildProgrammableObjectSummary(schemaName, objectName, filterType, modifyDate, definition);
            results.Add(summary);
        }

        return results;
    }

    private async Task<SchemaObjectSummary?> GetSingleProgrammableObjectAsync(
        SqlConnection connection,
        string schemaName,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SingleProgrammableObjectQuery, connection);
        command.Parameters.AddWithValue("@schemaName", schemaName);
        command.Parameters.AddWithValue("@objectName", objectName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sqlType = reader.GetString(2).Trim();
            var modifyDate = reader.GetDateTime(3);
            var definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            if (SqlTypeMap.TryGetValue(sqlType, out var objectType))
            {
                return BuildProgrammableObjectSummary(schemaName, objectName, objectType, modifyDate, definition);
            }
        }

        return null;
    }

    private async Task<List<SchemaObjectSummary>> GetTablesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var results = new List<SchemaObjectSummary>();

        await using var command = new SqlCommand(TablesQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<(string SchemaName, string TableName, DateTime ModifyDate)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Trim names to match SQL Server's lenient name resolution behavior
            var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0).Trim();
            var tableName = reader.GetString(1).Trim();
            var modifyDate = reader.GetDateTime(2);
            tables.Add((schemaName, tableName, modifyDate));
        }

        // Close reader before making additional queries
        await reader.CloseAsync().ConfigureAwait(false);

        foreach (var (schemaName, tableName, modifyDate) in tables)
        {
            var definition = await TableScriptBuilder.BuildCreateTableScriptAsync(
                connection, schemaName, tableName, cancellationToken).ConfigureAwait(false);

            var summary = BuildTableSummary(schemaName, tableName, modifyDate, definition);
            results.Add(summary);
        }

        return results;
    }

    private async Task<SchemaObjectSummary?> GetSingleTableAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var query = @"
            SELECT
                SCHEMA_NAME(t.schema_id) AS SchemaName,
                t.name AS TableName,
                t.modify_date AS ModifyDate
            FROM sys.tables t
            WHERE t.name = @tableName
              AND SCHEMA_NAME(t.schema_id) = @schemaName
              AND t.is_ms_shipped = 0";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@schemaName", schemaName);
        command.Parameters.AddWithValue("@tableName", tableName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var modifyDate = reader.GetDateTime(2);
            await reader.CloseAsync().ConfigureAwait(false);

            var definition = await TableScriptBuilder.BuildCreateTableScriptAsync(
                connection, schemaName, tableName, cancellationToken).ConfigureAwait(false);

            return BuildTableSummary(schemaName, tableName, modifyDate, definition);
        }

        return null;
    }

    private async Task<List<SchemaObjectSummary>> GetIndexesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        return await IndexScriptBuilder.GetAllIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SchemaObjectSummary?> GetSingleIndexAsync(
        SqlConnection connection,
        string schemaName,
        string indexObjectName,
        CancellationToken cancellationToken)
    {
        // Index object names are in format "TableName.IndexName"
        var parts = indexObjectName.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        var tableName = parts[0];
        var indexName = parts[1];

        return await IndexScriptBuilder.GetSingleIndexAsync(
            connection, schemaName, tableName, indexName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<SchemaObjectSummary>> GetUsersAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var results = new List<SchemaObjectSummary>();

        await using var command = new SqlCommand(UsersQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var userName = reader.GetString(0).Trim();
            var typeDesc = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var defaultSchema = reader.IsDBNull(2) ? null : reader.GetString(2)?.Trim();

            var definition = BuildUserScript(userName, defaultSchema);
            var summary = BuildSecurityPrincipalSummary(string.Empty, userName, SqlObjectType.User, definition);
            results.Add(summary);
        }

        return results;
    }

    private async Task<SchemaObjectSummary?> GetSingleUserAsync(
        SqlConnection connection,
        string userName,
        CancellationToken cancellationToken)
    {
        var query = @"
            SELECT
                name AS UserName,
                type_desc AS TypeDescription,
                default_schema_name AS DefaultSchema
            FROM sys.database_principals
            WHERE name = @userName
              AND type IN ('S', 'U', 'G', 'E', 'X')
              AND is_fixed_role = 0";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@userName", userName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var typeDesc = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var defaultSchema = reader.IsDBNull(2) ? null : reader.GetString(2)?.Trim();

            var definition = BuildUserScript(userName, defaultSchema);
            return BuildSecurityPrincipalSummary(string.Empty, userName, SqlObjectType.User, definition);
        }

        return null;
    }

    private async Task<List<SchemaObjectSummary>> GetRolesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var results = new List<SchemaObjectSummary>();

        await using var command = new SqlCommand(RolesQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var roleName = reader.GetString(0).Trim();
            var definition = $"CREATE ROLE [{roleName}]";
            var summary = BuildSecurityPrincipalSummary(string.Empty, roleName, SqlObjectType.Role, definition);
            results.Add(summary);
        }

        return results;
    }

    private async Task<SchemaObjectSummary?> GetSingleRoleAsync(
        SqlConnection connection,
        string roleName,
        CancellationToken cancellationToken)
    {
        var query = @"
            SELECT name
            FROM sys.database_principals
            WHERE name = @roleName
              AND type = 'R'
              AND is_fixed_role = 0";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@roleName", roleName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var definition = $"CREATE ROLE [{roleName}]";
            return BuildSecurityPrincipalSummary(string.Empty, roleName, SqlObjectType.Role, definition);
        }

        return null;
    }

    // Helper methods for building SchemaObjectSummary instances

    private SchemaObjectSummary BuildProgrammableObjectSummary(
        string schemaName,
        string objectName,
        SqlObjectType objectType,
        DateTime modifyDate,
        string definition)
    {
        // Apply the same normalization as the file side
        string normalizedDefinition;
        if (objectType == SqlObjectType.Trigger)
        {
            var normalized = SqlScriptNormalizer.Normalize(definition);
            var firstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(normalized);
            normalizedDefinition = SqlScriptNormalizer.NormalizeForComparison(firstBatch);
        }
        else
        {
            normalizedDefinition = SqlScriptNormalizer.NormalizeForComparison(definition);
        }

        var definitionHash = ComputeSha256(Encoding.UTF8.GetBytes(normalizedDefinition));

        return new SchemaObjectSummary
        {
            SchemaName = schemaName,
            ObjectName = objectName,
            ObjectType = objectType,
            ModifiedDate = modifyDate,
            DefinitionHash = definitionHash,
            DefinitionScript = normalizedDefinition
        };
    }

    private SchemaObjectSummary BuildTableSummary(
        string schemaName,
        string tableName,
        DateTime modifyDate,
        string definition)
    {
        // Apply the same normalization pipeline as DacFx did for tables
        var normalized = SqlScriptNormalizer.Normalize(definition);
        var firstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(normalized);
        var withoutConstraints = SqlScriptNormalizer.StripInlineConstraints(firstBatch);
        var normalizedDefinition = SqlScriptNormalizer.NormalizeForComparison(withoutConstraints);

        var definitionHash = ComputeSha256(Encoding.UTF8.GetBytes(normalizedDefinition));

        return new SchemaObjectSummary
        {
            SchemaName = schemaName,
            ObjectName = tableName,
            ObjectType = SqlObjectType.Table,
            ModifiedDate = modifyDate,
            DefinitionHash = definitionHash,
            DefinitionScript = normalizedDefinition
        };
    }

    private static SchemaObjectSummary BuildSecurityPrincipalSummary(
        string schemaName,
        string principalName,
        SqlObjectType objectType,
        string definition)
    {
        var normalizedDefinition = SqlScriptNormalizer.NormalizeForComparison(definition);
        var definitionHash = ComputeSha256(Encoding.UTF8.GetBytes(normalizedDefinition));

        return new SchemaObjectSummary
        {
            SchemaName = schemaName,
            ObjectName = principalName,
            ObjectType = objectType,
            ModifiedDate = null,
            DefinitionHash = definitionHash,
            DefinitionScript = normalizedDefinition
        };
    }

    private static string BuildUserScript(string userName, string? defaultSchema)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE USER [{userName}]");

        if (!string.IsNullOrEmpty(defaultSchema))
        {
            sb.Append($" WITH DEFAULT_SCHEMA = [{defaultSchema}]");
        }

        return sb.ToString();
    }

    private static string? GetSqlTypeCode(SqlObjectType objectType)
    {
        return objectType switch
        {
            SqlObjectType.StoredProcedure => "P",
            SqlObjectType.View => "V",
            SqlObjectType.ScalarFunction => "FN",
            SqlObjectType.TableValuedFunction => "TF",
            SqlObjectType.InlineTableValuedFunction => "IF",
            SqlObjectType.Trigger => "TR",
            _ => null
        };
    }

    /// <summary>
    /// Gets the CLR-specific SQL Server type code for an object type.
    /// CLR objects have different type codes than T-SQL objects:
    /// PC = CLR stored procedure, FS = CLR scalar function, FT = CLR table-valued function.
    /// </summary>
    private static string? GetClrTypeCode(SqlObjectType objectType)
    {
        return objectType switch
        {
            SqlObjectType.StoredProcedure => "PC",
            SqlObjectType.ScalarFunction => "FS",
            SqlObjectType.TableValuedFunction => "FT",
            // No CLR equivalents for these types
            SqlObjectType.View => null,
            SqlObjectType.InlineTableValuedFunction => null,
            SqlObjectType.Trigger => null,
            _ => null
        };
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}
