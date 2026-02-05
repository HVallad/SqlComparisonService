using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Domain.Changes;

/// <summary>
/// Identifies a specific database object to query or compare.
/// Used for batching multiple object queries efficiently.
/// </summary>
/// <param name="SchemaName">The schema name (e.g., "dbo").</param>
/// <param name="ObjectName">The object name (e.g., "MyProcedure").</param>
/// <param name="ObjectType">The type of SQL object.</param>
public record ObjectIdentifier(string SchemaName, string ObjectName, SqlObjectType ObjectType)
{
    /// <summary>
    /// Creates an ObjectIdentifier from a fully qualified name like "dbo.MyProcedure".
    /// </summary>
    public static ObjectIdentifier Parse(string fullName, SqlObjectType objectType)
    {
        var parts = fullName.Split('.', 2);
        var schemaName = parts.Length > 1 ? parts[0] : "dbo";
        var objectName = parts.Length > 1 ? parts[1] : parts[0];
        return new ObjectIdentifier(schemaName, objectName, objectType);
    }

    /// <summary>
    /// Gets the fully qualified name in format "schema.name".
    /// </summary>
    public string FullName => $"{SchemaName}.{ObjectName}";

    /// <summary>
    /// Creates a unique key for this object combining type and name.
    /// </summary>
    public string ToKey() => $"{ObjectType}:{SchemaName}.{ObjectName}";
}

