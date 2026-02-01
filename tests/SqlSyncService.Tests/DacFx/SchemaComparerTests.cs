using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Tests.DacFx;

public class SchemaComparerTests
{
    [Fact]
    public async Task CompareAsync_Returns_No_Differences_When_Schemas_Match()
    {
        // Arrange
        var comparer = new SchemaComparer();
        var subscriptionId = Guid.NewGuid();

        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            Objects =
            {
                new SchemaObjectSummary
                {
                    SchemaName = "dbo",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    DefinitionHash = "hash-users"
                }
            }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            CapturedAt = snapshot.CapturedAt,
            FileEntries =
            {
                ["Users.sql"] = new FileObjectEntry
                {
                    FilePath = "Users.sql",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    ContentHash = "hash-users",
                    LastModified = DateTime.UtcNow
                }
            }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        Assert.Empty(differences);
    }

    [Fact]
    public async Task CompareAsync_Detects_Add_Modify_And_Delete()
    {
        // Arrange
        var comparer = new SchemaComparer();
        var subscriptionId = Guid.NewGuid();

        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            Objects =
            {
                new SchemaObjectSummary
                {
                    SchemaName = "dbo",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    DefinitionHash = "hash-users-old"
                },
                new SchemaObjectSummary
                {
                    SchemaName = "dbo",
                    ObjectName = "Customers",
                    ObjectType = SqlObjectType.Table,
                    DefinitionHash = "hash-customers"
                }
            }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
            {
                ["Users.sql"] = new FileObjectEntry
                {
                    FilePath = "Users.sql",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    ContentHash = "hash-users-new",
                    LastModified = DateTime.UtcNow
                },
                ["Orders.sql"] = new FileObjectEntry
                {
                    FilePath = "Orders.sql",
                    ObjectName = "Orders",
                    ObjectType = SqlObjectType.Table,
                    ContentHash = "hash-orders",
                    LastModified = DateTime.UtcNow
                }
            }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        Assert.Equal(3, differences.Count);
        Assert.Contains(differences, d => d.ObjectName == "Users" && d.DifferenceType == DifferenceType.Modify);
        Assert.Contains(differences, d => d.ObjectName == "Customers" && d.DifferenceType == DifferenceType.Delete);
        Assert.Contains(differences, d => d.ObjectName == "Orders" && d.DifferenceType == DifferenceType.Add);
    }

    [Fact]
    public async Task CompareAsync_Honors_ComparisonOptions()
    {
        // Arrange
        var comparer = new SchemaComparer();
        var subscriptionId = Guid.NewGuid();

        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            Objects =
            {
                new SchemaObjectSummary
                {
                    SchemaName = "dbo",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    DefinitionHash = "hash-users"
                }
            }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
            {
                ["Users.sql"] = new FileObjectEntry
                {
                    FilePath = "Users.sql",
                    ObjectName = "Users",
                    ObjectType = SqlObjectType.Table,
                    ContentHash = "hash-users-new",
                    LastModified = DateTime.UtcNow
                }
            }
        };

        var options = new ComparisonOptions
        {
            IncludeTables = false,
            IncludeViews = true,
            IncludeStoredProcedures = true,
            IncludeFunctions = true,
            IncludeTriggers = true
        };

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        Assert.Empty(differences);
    }
}

