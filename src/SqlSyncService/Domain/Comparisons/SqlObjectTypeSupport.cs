using System.Collections.Generic;

namespace SqlSyncService.Domain.Comparisons;

/// <summary>
/// Centralizes knowledge about which SQL object types are supported for comparison.
/// </summary>
public static class SqlObjectTypeSupport
{
    /// <summary>
    /// Types that participate in schema comparison and can produce differences.
    /// </summary>
    public static readonly ISet<SqlObjectType> SupportedComparisonTypes = new HashSet<SqlObjectType>
    {
        SqlObjectType.Table,
        SqlObjectType.View,
        SqlObjectType.StoredProcedure,
        SqlObjectType.ScalarFunction,
        SqlObjectType.TableValuedFunction,
        SqlObjectType.InlineTableValuedFunction,
        SqlObjectType.Trigger,
        SqlObjectType.User,
        SqlObjectType.Role,
        SqlObjectType.Index
    };

    /// <summary>
    /// Returns true when the given type participates in comparison.
    /// </summary>
    public static bool IsSupportedForComparison(SqlObjectType type) => SupportedComparisonTypes.Contains(type);
}

