namespace SqlSyncService.Domain.Comparisons;

public class SchemaDifference
{
    public Guid Id { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public SqlObjectType ObjectType { get; set; }
    public DifferenceType DifferenceType { get; set; }
    public DifferenceSource Source { get; set; }

    public string? DatabaseDefinition { get; set; }
    public string? FileDefinition { get; set; }
    public string? FilePath { get; set; }

    public List<PropertyDifference>? PropertyChanges { get; set; }
}

public enum SqlObjectType
{
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    InlineTableValuedFunction,
    Trigger,
    Index,
    Constraint,
    UserDefinedType,
    Schema,
    Synonym,
    Login,
    Role,
    Unknown,
    User
}

public enum DifferenceType
{
    Add,
    Delete,
    Modify,
    Rename
}

public enum DifferenceSource
{
    Database,
    FileSystem
}

public class PropertyDifference
{
    public string PropertyName { get; set; } = string.Empty;
    public string? DatabaseValue { get; set; }
    public string? FileValue { get; set; }
}

