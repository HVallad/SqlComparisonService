using Microsoft.Data.SqlClient;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.DacFx;

/// <summary>
/// Builds CREATE INDEX scripts from SQL Server system views.
/// </summary>
internal static class IndexScriptBuilder
{
    // Query for all user-defined indexes (excluding primary keys and unique constraints which are table-level)
    // Includes data compression information from sys.partitions (using partition_number = 1 for non-partitioned indexes)
    // and filter definition for filtered indexes (has_filter, filter_definition)
    private const string AllIndexesQuery = @"
        SELECT
            SCHEMA_NAME(t.schema_id) AS SchemaName,
            t.name AS TableName,
            i.name AS IndexName,
            i.type_desc AS IndexType,
            i.is_unique,
            ISNULL(p.data_compression_desc, 'NONE') AS DataCompression,
            i.has_filter,
            i.filter_definition
        FROM sys.indexes i
        JOIN sys.tables t ON i.object_id = t.object_id
        LEFT JOIN sys.partitions p ON i.object_id = p.object_id
            AND i.index_id = p.index_id
            AND p.partition_number = 1
        WHERE i.name IS NOT NULL
          AND i.is_primary_key = 0
          AND i.is_unique_constraint = 0
          AND t.is_ms_shipped = 0
        ORDER BY SCHEMA_NAME(t.schema_id), t.name, i.name";

    // Query for index columns
    private const string IndexColumnsQuery = @"
        SELECT 
            COL_NAME(ic.object_id, ic.column_id) AS ColumnName,
            ic.is_descending_key,
            ic.is_included_column
        FROM sys.indexes i
        JOIN sys.tables t ON i.object_id = t.object_id
        JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.name = @indexName
          AND t.name = @tableName
          AND SCHEMA_NAME(t.schema_id) = @schemaName
        ORDER BY ic.key_ordinal, ic.index_column_id";

    public static async Task<List<SchemaObjectSummary>> GetAllIndexesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SchemaObjectSummary>();

        // First get all index metadata
        await using var command = new SqlCommand(AllIndexesQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var indexes = new List<IndexMetadata>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexes.Add(new IndexMetadata
            {
                SchemaName = reader.GetString(0),
                TableName = reader.GetString(1),
                IndexName = reader.GetString(2),
                IndexType = reader.GetString(3),
                IsUnique = reader.GetBoolean(4),
                DataCompression = reader.GetString(5),
                HasFilter = reader.GetBoolean(6),
                FilterDefinition = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        await reader.CloseAsync().ConfigureAwait(false);

        // Now get columns for each index
        foreach (var index in indexes)
        {
            var columns = await GetIndexColumnsAsync(
                connection, index.SchemaName, index.TableName, index.IndexName, cancellationToken
            ).ConfigureAwait(false);

            var script = BuildIndexScript(index, columns);
            var summary = BuildIndexSummary(index, script);
            results.Add(summary);
        }

        return results;
    }

    public static async Task<SchemaObjectSummary?> GetSingleIndexAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        // Get index metadata including data compression and filter definition
        var query = @"
            SELECT
                SCHEMA_NAME(t.schema_id) AS SchemaName,
                t.name AS TableName,
                i.name AS IndexName,
                i.type_desc AS IndexType,
                i.is_unique,
                ISNULL(p.data_compression_desc, 'NONE') AS DataCompression,
                i.has_filter,
                i.filter_definition
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            LEFT JOIN sys.partitions p ON i.object_id = p.object_id
                AND i.index_id = p.index_id
                AND p.partition_number = 1
            WHERE i.name = @indexName
              AND t.name = @tableName
              AND SCHEMA_NAME(t.schema_id) = @schemaName";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@schemaName", schemaName);
        command.Parameters.AddWithValue("@tableName", tableName);
        command.Parameters.AddWithValue("@indexName", indexName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var index = new IndexMetadata
        {
            SchemaName = reader.GetString(0),
            TableName = reader.GetString(1),
            IndexName = reader.GetString(2),
            IndexType = reader.GetString(3),
            IsUnique = reader.GetBoolean(4),
            DataCompression = reader.GetString(5),
            HasFilter = reader.GetBoolean(6),
            FilterDefinition = reader.IsDBNull(7) ? null : reader.GetString(7)
        };

        await reader.CloseAsync().ConfigureAwait(false);

        var columns = await GetIndexColumnsAsync(
            connection, schemaName, tableName, indexName, cancellationToken
        ).ConfigureAwait(false);

        var script = BuildIndexScript(index, columns);
        return BuildIndexSummary(index, script);
    }

    private static async Task<List<IndexColumnInfo>> GetIndexColumnsAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var columns = new List<IndexColumnInfo>();

        await using var command = new SqlCommand(IndexColumnsQuery, connection);
        command.Parameters.AddWithValue("@schemaName", schemaName);
        command.Parameters.AddWithValue("@tableName", tableName);
        command.Parameters.AddWithValue("@indexName", indexName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new IndexColumnInfo
            {
                ColumnName = reader.GetString(0),
                IsDescending = reader.GetBoolean(1),
                IsIncluded = reader.GetBoolean(2)
            });
        }

        return columns;
    }

    private static string BuildIndexScript(IndexMetadata index, List<IndexColumnInfo> columns)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE ");

        if (index.IsUnique)
        {
            sb.Append("UNIQUE ");
        }

        // Determine index type
        if (index.IndexType.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase) &&
            !index.IndexType.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("CLUSTERED ");
        }
        else
        {
            sb.Append("NONCLUSTERED ");
        }

        sb.Append($"INDEX [{index.IndexName}] ON [{index.SchemaName}].[{index.TableName}]");

        // Key columns
        var keyColumns = columns.Where(c => !c.IsIncluded).ToList();
        if (keyColumns.Count > 0)
        {
            sb.Append(" (");
            sb.Append(string.Join(", ", keyColumns.Select(c =>
                c.IsDescending ? $"[{c.ColumnName}] DESC" : $"[{c.ColumnName}] ASC")));
            sb.Append(')');
        }

        // Included columns
        var includedColumns = columns.Where(c => c.IsIncluded).ToList();
        if (includedColumns.Count > 0)
        {
            sb.Append(" INCLUDE (");
            sb.Append(string.Join(", ", includedColumns.Select(c => $"[{c.ColumnName}]")));
            sb.Append(')');
        }

        // WHERE clause for filtered indexes
        if (index.HasFilter && !string.IsNullOrEmpty(index.FilterDefinition))
        {
            sb.Append($" WHERE {index.FilterDefinition}");
        }

        // WITH clause for index options (e.g., DATA_COMPRESSION)
        if (!string.IsNullOrEmpty(index.DataCompression) &&
            !index.DataCompression.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($" WITH (DATA_COMPRESSION = {index.DataCompression})");
        }

        return sb.ToString();
    }

    private static SchemaObjectSummary BuildIndexSummary(IndexMetadata index, string script)
    {
        var normalizedScript = SqlScriptNormalizer.NormalizeIndexForComparison(script);
        var definitionHash = ComputeSha256(Encoding.UTF8.GetBytes(normalizedScript));

        // Index object names follow the pattern "TableName.IndexName" for uniqueness
        var objectName = $"{index.TableName}.{index.IndexName}";

        return new SchemaObjectSummary
        {
            SchemaName = index.SchemaName,
            ObjectName = objectName,
            ObjectType = SqlObjectType.Index,
            ModifiedDate = null,
            DefinitionHash = definitionHash,
            DefinitionScript = normalizedScript
        };
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Index metadata from sys.indexes.
/// </summary>
internal class IndexMetadata
{
    public string SchemaName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;
    public string IndexType { get; set; } = string.Empty;
    public bool IsUnique { get; set; }
    public string DataCompression { get; set; } = "NONE";
    public bool HasFilter { get; set; }
    public string? FilterDefinition { get; set; }
}

/// <summary>
/// Index column information from sys.index_columns.
/// </summary>
internal class IndexColumnInfo
{
    public string ColumnName { get; set; } = string.Empty;
    public bool IsDescending { get; set; }
    public bool IsIncluded { get; set; }
}
