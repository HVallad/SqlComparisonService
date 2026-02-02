using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Comparisons;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Persistence;
using System.Linq;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ComparisonsController : ControllerBase
{
    private readonly IComparisonHistoryRepository _historyRepository;

    public ComparisonsController(IComparisonHistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ComparisonDetailResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var comparison = await _historyRepository
            .GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (comparison is null)
        {
            var error = new ErrorDetail
            {
                Code = ErrorCodes.NotFound,
                Message = $"Comparison '{id}' was not found.",
                TraceId = HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            return NotFound(new ErrorResponse { Error = error });
        }

        var response = MapToDetailResponse(comparison);
        return Ok(response);
    }

	    [HttpGet("{id:guid}/differences")]
	    public async Task<ActionResult<GetComparisonDifferencesResponse>> GetDifferences(
	        Guid id,
	        [FromQuery] string? type,
	        [FromQuery] string? action,
	        [FromQuery] string? direction,
	        CancellationToken cancellationToken)
	    {
	        var comparison = await _historyRepository
	            .GetByIdAsync(id, cancellationToken)
	            .ConfigureAwait(false);

	        if (comparison is null)
	        {
	            var error = new ErrorDetail
	            {
	                Code = ErrorCodes.NotFound,
	                Message = $"Comparison '{id}' was not found.",
	                TraceId = HttpContext.TraceIdentifier,
	                Timestamp = DateTime.UtcNow
	            };

	            return NotFound(new ErrorResponse { Error = error });
	        }

	        var differences = comparison.Differences ?? new List<SchemaDifference>();

	        if (!string.IsNullOrWhiteSpace(type))
	        {
	            var normalizedType = type.Trim().ToLowerInvariant();
	            differences = differences
	                .Where(d => string.Equals(
	                    MapObjectTypeKey(d.ObjectType.ToString()),
	                    normalizedType,
	                    StringComparison.OrdinalIgnoreCase))
	                .ToList();
	        }

	        if (!string.IsNullOrWhiteSpace(action))
	        {
	            var normalizedAction = action.Trim().ToLowerInvariant();
	            if (normalizedAction == "modify")
	            {
	                normalizedAction = "change";
	            }

	            differences = differences
	                .Where(d => string.Equals(
	                    MapDifferenceAction(d.DifferenceType),
	                    normalizedAction,
	                    StringComparison.OrdinalIgnoreCase))
	                .ToList();
	        }

		    if (!string.IsNullOrWhiteSpace(direction))
		    {
		        // Accept both "database-only" and "databaseOnly" style values.
		        var normalizedDirectionKey = direction.Trim().Replace("-", string.Empty).ToLowerInvariant();
		        differences = differences
		            .Where(d =>
		            {
		                var key = MapDirection(d);
		                key = key.Replace("-", string.Empty).ToLowerInvariant();
		                return key == normalizedDirectionKey;
		            })
		            .ToList();
		    }

	        var items = differences
	            .Select(MapToDifferenceListItem)
	            .ToList();

	        var response = new GetComparisonDifferencesResponse
	        {
	            ComparisonId = comparison.Id,
	            Differences = items,
	            TotalCount = items.Count
	        };

	        return Ok(response);
	    }

	    [HttpGet("{id:guid}/differences/{diffId:guid}")]
	    public async Task<ActionResult<ComparisonDifferenceDetailResponse>> GetDifferenceById(
	        Guid id,
	        Guid diffId,
	        CancellationToken cancellationToken)
	    {
	        var comparison = await _historyRepository
	            .GetByIdAsync(id, cancellationToken)
	            .ConfigureAwait(false);

	        if (comparison is null)
	        {
	            var error = new ErrorDetail
	            {
	                Code = ErrorCodes.NotFound,
	                Message = $"Comparison '{id}' was not found.",
	                TraceId = HttpContext.TraceIdentifier,
	                Timestamp = DateTime.UtcNow
	            };

	            return NotFound(new ErrorResponse { Error = error });
	        }

	        var difference = comparison.Differences?.FirstOrDefault(d => d.Id == diffId);
	        if (difference is null)
	        {
	            var error = new ErrorDetail
	            {
	                Code = ErrorCodes.NotFound,
	                Message = $"Difference '{diffId}' was not found in comparison '{id}'.",
	                TraceId = HttpContext.TraceIdentifier,
	                Timestamp = DateTime.UtcNow
	            };

	            return NotFound(new ErrorResponse { Error = error });
	        }

	        var response = MapToDifferenceDetailResponse(comparison, difference);
	        return Ok(response);
	    }

    private static ComparisonDetailResponse MapToDetailResponse(ComparisonResult comparison)
    {
        return new ComparisonDetailResponse
        {
            Id = comparison.Id,
            SubscriptionId = comparison.SubscriptionId,
            Status = MapComparisonStatus(comparison.Status),
            ComparedAt = comparison.ComparedAt,
            Duration = System.Xml.XmlConvert.ToString(comparison.Duration),
            DifferenceCount = comparison.Summary.TotalDifferences,
            Summary = BuildSummary(comparison)
        };
    }

    private static ComparisonSummaryResponse BuildSummary(ComparisonResult comparison)
    {
	        var summary = new ComparisonSummaryResponse
	        {
	            TotalDifferences = comparison.Summary.TotalDifferences,
	            ObjectsCompared = comparison.Summary.ObjectsCompared,
		            ObjectsUnchanged = comparison.Summary.ObjectsUnchanged,
		            UnsupportedDatabaseObjectCount = comparison.Summary.UnsupportedDatabaseObjectCount,
		            UnsupportedFileObjectCount = comparison.Summary.UnsupportedFileObjectCount
	        };

        foreach (var group in comparison.Summary.ByObjectType)
        {
            var key = MapObjectTypeKey(group.Key);
            summary.ByType[key] = group.Value;
        }

        summary.ByAction["add"] = comparison.Summary.Additions;
        summary.ByAction["modify"] = comparison.Summary.Modifications;
        summary.ByAction["delete"] = comparison.Summary.Deletions;

        var differences = comparison.Differences ?? new List<SchemaDifference>();

        summary.ByDirection["database-only"] = differences.Count(d => d.Source == DifferenceSource.Database);
        summary.ByDirection["file-only"] = differences.Count(d => d.Source == DifferenceSource.FileSystem && d.DifferenceType == DifferenceType.Add);
        summary.ByDirection["different"] = differences.Count(d => d.DifferenceType == DifferenceType.Modify);

	        return summary;
	    }

	    [HttpGet("{id:guid}/unsupported-objects")]
	    public async Task<ActionResult<GetUnsupportedObjectsResponse>> GetUnsupportedObjects(
	        Guid id,
	        CancellationToken cancellationToken)
	    {
	        var comparison = await _historyRepository
	            .GetByIdAsync(id, cancellationToken)
	            .ConfigureAwait(false);

	        if (comparison is null)
	        {
	            var error = new ErrorDetail
	            {
	                Code = ErrorCodes.NotFound,
	                Message = $"Comparison '{id}' was not found.",
	                TraceId = HttpContext.TraceIdentifier,
	                Timestamp = DateTime.UtcNow
	            };

	            return NotFound(new ErrorResponse { Error = error });
	        }

	        var response = BuildUnsupportedObjectsResponse(comparison);
	        return Ok(response);
	    }

	    private static ComparisonDifferenceListItemResponse MapToDifferenceListItem(SchemaDifference difference)
	    {
	        var objectType = MapObjectTypeKey(difference.ObjectType.ToString());
	        var action = MapDifferenceAction(difference.DifferenceType);
	        var direction = MapDirection(difference);
	        var objectName = BuildObjectName(difference);
	        var description = BuildDifferenceDescription(difference, action, direction);
	        var severity = action == "change" ? "warning" : "info";

	        return new ComparisonDifferenceListItemResponse
	        {
	            Id = difference.Id,
	            ObjectType = objectType,
	            ObjectName = objectName,
	            Action = action,
	            Direction = direction,
	            Description = description,
	            Severity = severity,
	            FilePath = difference.FilePath,
	            SuggestedFilePath = BuildSuggestedFilePath(difference),
	            ChangeDetails = null
	        };
	    }

	    private static ComparisonDifferenceDetailResponse MapToDifferenceDetailResponse(
	        ComparisonResult comparison,
	        SchemaDifference difference)
	    {
	        var objectType = MapObjectTypeKey(difference.ObjectType.ToString());
	        var action = MapDifferenceAction(difference.DifferenceType);
	        var direction = MapDirection(difference);
	        var objectName = BuildObjectName(difference);

	        return new ComparisonDifferenceDetailResponse
	        {
	            Id = difference.Id,
	            ComparisonId = comparison.Id,
	            SubscriptionId = comparison.SubscriptionId,
	            ObjectType = objectType,
	            ObjectName = objectName,
	            Action = action,
	            Direction = direction,
	            FilePath = difference.FilePath,
	            DatabaseScript = difference.DatabaseDefinition,
	            FileScript = difference.FileDefinition,
	            UnifiedDiff = null,
	            SideBySideDiff = null,
	            PropertyChanges = (difference.PropertyChanges ?? new List<PropertyDifference>())
	                .Select(p => new PropertyDifferenceResponse
	                {
	                    PropertyName = p.PropertyName,
	                    DatabaseValue = p.DatabaseValue,
	                    FileValue = p.FileValue
	                })
	                .ToList()
	        };
	    }

	    private static string MapObjectTypeKey(string domainKey)
	    {
	        if (string.IsNullOrWhiteSpace(domainKey))
	        {
	            return "unknown";
	        }

	        return domainKey switch
	        {
	            "Table" => "table",
	            "View" => "view",
	            "StoredProcedure" => "stored-procedure",
	            "ScalarFunction" or "TableValuedFunction" or "InlineTableValuedFunction" => "function",
	            "Trigger" => "trigger",
	            _ => domainKey.ToLowerInvariant()
	        };
	    }

	    private static string MapDifferenceAction(DifferenceType type)
	    {
	        return type switch
	        {
	            DifferenceType.Add => "add",
	            DifferenceType.Delete => "delete",
	            DifferenceType.Modify or DifferenceType.Rename => "change",
	            _ => "change"
	        };
	    }

		    private static string MapDirection(SchemaDifference difference)
		    {
		        if (difference.DifferenceType == DifferenceType.Modify || difference.DifferenceType == DifferenceType.Rename)
		        {
		            return "different";
		        }

		        return difference.Source switch
		        {
		            DifferenceSource.Database => "database-only",
		            DifferenceSource.FileSystem => "file-only",
		            _ => "different"
		        };
		    }

	    private static string BuildObjectName(SchemaDifference difference)
	    {
	        if (string.IsNullOrWhiteSpace(difference.ObjectName))
	        {
	            return difference.ObjectName;
	        }

	        // If the object name already contains a dot, assume it is schema-qualified.
	        if (difference.ObjectName.Contains('.'))
	        {
	            return difference.ObjectName;
	        }

	        if (!string.IsNullOrWhiteSpace(difference.SchemaName))
	        {
	            return $"{difference.SchemaName}.{difference.ObjectName}";
	        }

	        return difference.ObjectName;
	    }

		    private static string BuildDifferenceDescription(
		        SchemaDifference difference,
		        string action,
		        string direction)
		    {
		        return (action, direction) switch
		        {
		            ("add", "file-only") => "Object exists in project files but not in database.",
		            ("add", "database-only") => "Object exists in database but not in project files.",
		            ("delete", "database-only") => "Object exists in database but not in project files.",
		            ("delete", "file-only") => "Object exists in project files but not in database.",
		            ("change", "different") => "Object definition differs between database and project files.",
		            _ => "Object difference detected between database and project files."
		        };
		    }

		    private static string? BuildSuggestedFilePath(SchemaDifference difference)
		    {
		        if (!string.IsNullOrEmpty(difference.FilePath))
		        {
		            // If we already know the file path, no suggestion is necessary.
		            return null;
		        }
		
		        var folder = difference.ObjectType switch
		        {
		            SqlObjectType.Table => "Tables",
		            SqlObjectType.View => "Views",
		            SqlObjectType.StoredProcedure => "StoredProcedures",
		            SqlObjectType.ScalarFunction or SqlObjectType.TableValuedFunction or SqlObjectType.InlineTableValuedFunction => "Functions",
		            SqlObjectType.Trigger => "Triggers",
		            _ => null
		        };
		
		        if (folder is null)
		        {
		            return null;
		        }
		
		        // Prefer schema/objectType/objectName when we know the schema, which
		        // aligns with the "by-schema-and-type" project layout
		        // (e.g. dbo/Tables/TestTable2.sql).
		        var schema = difference.SchemaName;
		        var objectName = difference.ObjectName;
		
		        if (!string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(objectName))
		        {
		            return $"{schema}/{folder}/{objectName}.sql";
		        }
		
		        // Fallback: use the schema-qualified name for project layouts that are
		        // only by type (e.g. Tables/dbo.TestTable2.sql).
		        var name = BuildObjectName(difference);
		        if (string.IsNullOrWhiteSpace(name))
		        {
		            return null;
		        }
		
		        return $"{folder}/{name}.sql";
		    }

	    private static string MapComparisonStatus(ComparisonStatus status)
	    {
	        return status switch
	        {
	            ComparisonStatus.Synchronized => "synchronized",
	            ComparisonStatus.HasDifferences => "has-differences",
	            ComparisonStatus.Error => "error",
	            ComparisonStatus.Partial => "partial",
	            _ => "unknown"
	        };
	    }

	    private static GetUnsupportedObjectsResponse BuildUnsupportedObjectsResponse(ComparisonResult comparison)
	    {
	        var unsupported = comparison.UnsupportedObjects ?? new List<UnsupportedObject>();

	        var databaseCount = unsupported.Count(o => o.Source == DifferenceSource.Database);
	        var fileCount = unsupported.Count(o => o.Source == DifferenceSource.FileSystem);

	        return new GetUnsupportedObjectsResponse
	        {
	            ComparisonId = comparison.Id,
	            Objects = unsupported
	                .Select(MapUnsupportedObject)
	                .ToList(),
	            TotalCount = unsupported.Count,
	            DatabaseCount = databaseCount,
	            FileCount = fileCount
	        };
	    }

	    private static UnsupportedObjectResponse MapUnsupportedObject(UnsupportedObject obj)
	    {
	        var source = obj.Source switch
	        {
	            DifferenceSource.Database => "database",
	            DifferenceSource.FileSystem => "file",
	            _ => "unknown"
	        };

	        return new UnsupportedObjectResponse
	        {
	            Source = source,
	            ObjectType = MapObjectTypeKey(obj.ObjectType.ToString()),
	            SchemaName = string.IsNullOrWhiteSpace(obj.SchemaName) ? null : obj.SchemaName,
	            ObjectName = obj.ObjectName,
	            FilePath = obj.FilePath
	        };
	    }
}

