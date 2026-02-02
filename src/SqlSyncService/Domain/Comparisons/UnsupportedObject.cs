namespace SqlSyncService.Domain.Comparisons;

public sealed class UnsupportedObject
{
    /// <summary>
    /// Indicates whether the object came from the database snapshot or the file system.
    /// </summary>
    public DifferenceSource Source { get; set; }

    /// <summary>
    /// The inferred SQL object type (may be Unknown or a non-whitelisted type such as Login).
    /// </summary>
    public SqlObjectType ObjectType { get; set; }

    /// <summary>
    /// The schema name, when available (database side only).
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// The logical object name (e.g. table, view, procedure, login, etc.).
    /// </summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>
    /// The project-relative file path when the object comes from the file system.
    /// </summary>
    public string? FilePath { get; set; }
}

