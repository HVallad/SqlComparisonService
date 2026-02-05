using Microsoft.Data.SqlClient;
using System.Text;

namespace SqlSyncService.DacFx;

/// <summary>
/// Builds CREATE TABLE scripts from SQL Server system views.
/// </summary>
internal static class TableScriptBuilder
{
    // Query for table columns with all metadata including temporal column info
    private const string ColumnsQuery = @"
        SELECT
            c.column_id,
            c.name AS ColumnName,
            t.name AS TypeName,
            c.max_length,
            c.precision,
            c.scale,
            c.is_nullable,
            c.is_identity,
            ic.seed_value,
            ic.increment_value,
            ISNULL(ic.is_not_for_replication, 0) AS is_not_for_replication,
            cc.definition AS ComputedDefinition,
            c.is_computed,
            c.generated_always_type
        FROM sys.columns c
        JOIN sys.types t ON c.user_type_id = t.user_type_id
        LEFT JOIN sys.identity_columns ic ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
        WHERE c.object_id = OBJECT_ID(@fullTableName)
        ORDER BY c.column_id";

    // Query for temporal table and memory-optimized table metadata
    private const string TableMetadataQuery = @"
        SELECT
            t.temporal_type,
            SCHEMA_NAME(ht.schema_id) AS HistorySchemaName,
            ht.name AS HistoryTableName,
            p.start_column_id,
            p.end_column_id,
            t.is_memory_optimized,
            t.durability
        FROM sys.tables t
        LEFT JOIN sys.tables ht ON t.history_table_id = ht.object_id
        LEFT JOIN sys.periods p ON t.object_id = p.object_id
        WHERE t.object_id = OBJECT_ID(@fullTableName)";

    public static async Task<string> BuildCreateTableScriptAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var fullTableName = $"[{schemaName}].[{tableName}]";
        var columns = new List<ColumnDefinition>();

        // Get column definitions
        await using (var command = new SqlCommand(ColumnsQuery, connection))
        {
            command.Parameters.AddWithValue("@fullTableName", fullTableName);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var column = new ColumnDefinition
                {
                    ColumnId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DataType = reader.GetString(2),
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt16(3),
                    Precision = reader.IsDBNull(4) ? null : reader.GetByte(4),
                    Scale = reader.IsDBNull(5) ? null : reader.GetByte(5),
                    IsNullable = reader.GetBoolean(6),
                    IsIdentity = reader.GetBoolean(7),
                    IdentitySeed = reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetValue(8)),
                    IdentityIncrement = reader.IsDBNull(9) ? null : Convert.ToInt64(reader.GetValue(9)),
                    IsNotForReplication = reader.GetBoolean(10),
                    ComputedDefinition = reader.IsDBNull(11) ? null : reader.GetString(11),
                    IsComputed = reader.GetBoolean(12),
                    GeneratedAlwaysType = reader.IsDBNull(13) ? (byte)0 : Convert.ToByte(reader.GetValue(13))
                };
                columns.Add(column);
            }
        }

        // Get temporal table and memory-optimized table metadata
        TemporalTableInfo? temporalInfo = null;
        MemoryOptimizedTableInfo? memoryOptimizedInfo = null;
        await using (var command = new SqlCommand(TableMetadataQuery, connection))
        {
            command.Parameters.AddWithValue("@fullTableName", fullTableName);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var temporalType = reader.GetByte(0);
                // temporal_type: 0 = non-temporal, 1 = history table, 2 = system-versioned

                // Read period column IDs from sys.periods (if available)
                var startColumnId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                var endColumnId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);

                // If sys.periods doesn't have the data, fall back to detecting columns
                // with generated_always_type 1 (ROW START) and 2 (ROW END)
                if (!startColumnId.HasValue || !endColumnId.HasValue)
                {
                    var startColumn = columns.FirstOrDefault(c => c.GeneratedAlwaysType == 1);
                    var endColumn = columns.FirstOrDefault(c => c.GeneratedAlwaysType == 2);
                    if (startColumn != null && endColumn != null)
                    {
                        startColumnId = startColumn.ColumnId;
                        endColumnId = endColumn.ColumnId;
                    }
                }

                // Create temporalInfo if we have period columns (regardless of temporal_type)
                // This ensures PERIOD FOR SYSTEM_TIME is generated for tables that had
                // system versioning disabled but still have GENERATED ALWAYS columns
                if (startColumnId.HasValue && endColumnId.HasValue)
                {
                    temporalInfo = new TemporalTableInfo
                    {
                        // Only include history table info if system versioning is currently enabled
                        HistorySchemaName = temporalType == 2 && !reader.IsDBNull(1) ? reader.GetString(1) : null,
                        HistoryTableName = temporalType == 2 && !reader.IsDBNull(2) ? reader.GetString(2) : null,
                        StartColumnId = startColumnId,
                        EndColumnId = endColumnId
                    };
                }

                // Check for memory-optimized table
                // is_memory_optimized: bit column
                var isMemoryOptimized = !reader.IsDBNull(5) && reader.GetBoolean(5);
                if (isMemoryOptimized)
                {
                    // durability: 0 = SCHEMA_AND_DATA, 1 = SCHEMA_ONLY
                    var durability = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6);
                    memoryOptimizedInfo = new MemoryOptimizedTableInfo
                    {
                        Durability = durability == 1
                            ? MemoryOptimizedDurability.SchemaOnly
                            : MemoryOptimizedDurability.SchemaAndData
                    };
                }
            }
        }

        return BuildScript(schemaName, tableName, columns, temporalInfo, memoryOptimizedInfo);
    }

    private static string BuildScript(
        string schemaName,
        string tableName,
        List<ColumnDefinition> columns,
        TemporalTableInfo? temporalInfo = null,
        MemoryOptimizedTableInfo? memoryOptimizedInfo = null)
    {
        if (columns.Count == 0)
        {
            return $"CREATE TABLE [{schemaName}].[{tableName}]\n(\n)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schemaName}].[{tableName}]");
        sb.AppendLine("(");

        // Determine if we need a PERIOD FOR SYSTEM_TIME clause
        var hasPeriodClause = temporalInfo != null &&
                              temporalInfo.StartColumnId.HasValue &&
                              temporalInfo.EndColumnId.HasValue;

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var isLastColumn = i == columns.Count - 1;
            var columnDef = BuildColumnDefinition(column);

            sb.Append("    ");
            sb.Append(columnDef);

            // Add comma if not last column, or if we have a PERIOD clause coming
            if (!isLastColumn || hasPeriodClause)
            {
                sb.Append(',');
            }
            sb.AppendLine();
        }

        // Add PERIOD FOR SYSTEM_TIME clause for temporal tables
        if (hasPeriodClause)
        {
            var startColumn = columns.FirstOrDefault(c => c.ColumnId == temporalInfo!.StartColumnId);
            var endColumn = columns.FirstOrDefault(c => c.ColumnId == temporalInfo!.EndColumnId);

            if (startColumn != null && endColumn != null)
            {
                sb.AppendLine($"    PERIOD FOR SYSTEM_TIME ([{startColumn.Name}], [{endColumn.Name}])");
            }
        }

        sb.Append(')');

        // Build the WITH clause if we have temporal or memory-optimized options
        var withOptions = new List<string>();

        // Memory-optimized table options
        if (memoryOptimizedInfo != null)
        {
            var durabilityValue = memoryOptimizedInfo.Durability == MemoryOptimizedDurability.SchemaOnly
                ? "SCHEMA_ONLY"
                : "SCHEMA_AND_DATA";
            withOptions.Add($"MEMORY_OPTIMIZED = ON");
            withOptions.Add($"DURABILITY = {durabilityValue}");
        }

        // Temporal table SYSTEM_VERSIONING option
        if (temporalInfo != null &&
            !string.IsNullOrEmpty(temporalInfo.HistorySchemaName) &&
            !string.IsNullOrEmpty(temporalInfo.HistoryTableName))
        {
            withOptions.Add($"SYSTEM_VERSIONING = ON (HISTORY_TABLE = [{temporalInfo.HistorySchemaName}].[{temporalInfo.HistoryTableName}], DATA_CONSISTENCY_CHECK = ON)");
        }

        if (withOptions.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"WITH ({string.Join(", ", withOptions)})");
        }

        return sb.ToString();
    }

    private static string BuildColumnDefinition(ColumnDefinition column)
    {
        var sb = new StringBuilder();
        sb.Append($"[{column.Name}]");

        if (column.IsComputed && !string.IsNullOrEmpty(column.ComputedDefinition))
        {
            // Computed column
            sb.Append($" AS {column.ComputedDefinition}");
        }
        else
        {
            // Regular column
            sb.Append(' ');
            sb.Append(FormatDataType(column.DataType, column.MaxLength, column.Precision, column.Scale));

            if (column.IsIdentity)
            {
                var seed = column.IdentitySeed ?? 1;
                var increment = column.IdentityIncrement ?? 1;
                sb.Append($" IDENTITY({seed},{increment})");

                if (column.IsNotForReplication)
                {
                    sb.Append(" NOT FOR REPLICATION");
                }
            }

            // Add GENERATED ALWAYS clause for temporal columns
            // generated_always_type: 0 = not generated, 1 = AS ROW START, 2 = AS ROW END
            if (column.GeneratedAlwaysType == 1)
            {
                sb.Append(" GENERATED ALWAYS AS ROW START");
            }
            else if (column.GeneratedAlwaysType == 2)
            {
                sb.Append(" GENERATED ALWAYS AS ROW END");
            }

            sb.Append(column.IsNullable ? " NULL" : " NOT NULL");
        }

        return sb.ToString();
    }

    private static string FormatDataType(string typeName, int? maxLength, int? precision, int? scale)
    {
        var upperType = typeName.ToUpperInvariant();

        // Types that need length
        if (upperType is "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "BINARY" or "VARBINARY")
        {
            if (maxLength == -1)
            {
                return $"{upperType}(MAX)";
            }

            // nvarchar/nchar use 2 bytes per character
            var displayLength = upperType.StartsWith("N") ? maxLength / 2 : maxLength;
            return $"{upperType}({displayLength})";
        }

        // Types that need precision and scale
        if (upperType is "DECIMAL" or "NUMERIC")
        {
            return $"{upperType}({precision},{scale})";
        }

        // Types that need scale only
        if (upperType is "DATETIME2" or "DATETIMEOFFSET" or "TIME")
        {
            if (scale != 7) // 7 is default
            {
                return $"{upperType}({scale})";
            }
            return upperType;
        }

        // Float with precision
        if (upperType == "FLOAT" && precision != 53)
        {
            return $"{upperType}({precision})";
        }

        // All other types
        return upperType;
    }
}

/// <summary>
/// Represents a column definition extracted from system views.
/// </summary>
internal class ColumnDefinition
{
    public int ColumnId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public long? IdentitySeed { get; set; }
    public long? IdentityIncrement { get; set; }
    public bool IsNotForReplication { get; set; }
    public string? ComputedDefinition { get; set; }
    public bool IsComputed { get; set; }

    /// <summary>
    /// SQL Server generated_always_type:
    /// 0 = Not a generated column
    /// 1 = GENERATED ALWAYS AS ROW START
    /// 2 = GENERATED ALWAYS AS ROW END
    /// </summary>
    public byte GeneratedAlwaysType { get; set; }
}

/// <summary>
/// Represents temporal table metadata for system-versioned tables.
/// </summary>
internal class TemporalTableInfo
{
    /// <summary>
    /// Schema name of the history table.
    /// </summary>
    public string? HistorySchemaName { get; set; }

    /// <summary>
    /// Name of the history table.
    /// </summary>
    public string? HistoryTableName { get; set; }

    /// <summary>
    /// Column ID of the PERIOD start column (ROW START).
    /// </summary>
    public int? StartColumnId { get; set; }

    /// <summary>
    /// Column ID of the PERIOD end column (ROW END).
    /// </summary>
    public int? EndColumnId { get; set; }
}

/// <summary>
/// Represents memory-optimized table metadata.
/// </summary>
internal class MemoryOptimizedTableInfo
{
    /// <summary>
    /// The durability option for the memory-optimized table.
    /// </summary>
    public MemoryOptimizedDurability Durability { get; set; }
}

/// <summary>
/// Durability options for memory-optimized tables.
/// </summary>
internal enum MemoryOptimizedDurability
{
    /// <summary>
    /// SCHEMA_AND_DATA: Both schema and data are durable (default).
    /// Data survives server restart.
    /// </summary>
    SchemaAndData = 0,

    /// <summary>
    /// SCHEMA_ONLY: Only schema is durable.
    /// Data is lost on server restart but schema remains.
    /// </summary>
    SchemaOnly = 1
}

