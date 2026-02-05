using System.ComponentModel.DataAnnotations;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Contracts.Comparisons;

/// <summary>
/// Request to compare a single database object against its file definition.
/// </summary>
public sealed class CompareObjectRequest
{
    /// <summary>
    /// Schema name of the object (e.g., "dbo").
    /// </summary>
    [Required]
    public string SchemaName { get; set; } = "dbo";

    /// <summary>
    /// Name of the object (e.g., "MyProcedure").
    /// </summary>
    [Required]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>
    /// Type of the object (e.g., StoredProcedure, Table, View).
    /// </summary>
    [Required]
    public SqlObjectType ObjectType { get; set; }
}

/// <summary>
/// Response from a single-object comparison.
/// </summary>
public sealed class CompareObjectResponse
{
    /// <summary>
    /// The subscription that was compared.
    /// </summary>
    public Guid SubscriptionId { get; set; }

    /// <summary>
    /// Schema name of the compared object.
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the compared object.
    /// </summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>
    /// Type of the compared object.
    /// </summary>
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the object is synchronized (no differences).
    /// </summary>
    public bool IsSynchronized { get; set; }

    /// <summary>
    /// The difference found, if any.
    /// </summary>
    public SchemaDifferenceDto? Difference { get; set; }

    /// <summary>
    /// Whether the object exists in the database.
    /// </summary>
    public bool ExistsInDatabase { get; set; }

    /// <summary>
    /// Whether the object exists in the file system.
    /// </summary>
    public bool ExistsInFileSystem { get; set; }

    /// <summary>
    /// Timestamp when the comparison was performed.
    /// </summary>
    public DateTime ComparedAt { get; set; }
}

/// <summary>
/// DTO for a schema difference in single-object comparison response.
/// </summary>
public sealed class SchemaDifferenceDto
{
    public string DifferenceType { get; set; } = string.Empty;
    public string? DatabaseDefinition { get; set; }
    public string? FileDefinition { get; set; }
    public string? FilePath { get; set; }
}

