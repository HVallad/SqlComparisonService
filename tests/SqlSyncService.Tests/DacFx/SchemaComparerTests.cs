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
	                    DefinitionHash = "hash-users-old",
	                    DefinitionScript = "CREATE TABLE dbo.Users (Id INT); -- old"
	                },
	                new SchemaObjectSummary
	                {
	                    SchemaName = "dbo",
	                    ObjectName = "Customers",
	                    ObjectType = SqlObjectType.Table,
	                    DefinitionHash = "hash-customers",
	                    DefinitionScript = "CREATE TABLE dbo.Customers (Id INT);"
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
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(50)); -- new"
	                },
	                ["Orders.sql"] = new FileObjectEntry
	                {
	                    FilePath = "Orders.sql",
	                    ObjectName = "Orders",
	                    ObjectType = SqlObjectType.Table,
	                    ContentHash = "hash-orders",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE TABLE dbo.Orders (Id INT);"
	                }
	            }
	        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

	        // Assert
	        Assert.Equal(3, differences.Count);
	        var modify = differences.Single(d => d.ObjectName == "Users" && d.DifferenceType == DifferenceType.Modify);
	        var delete = differences.Single(d => d.ObjectName == "Customers" && d.DifferenceType == DifferenceType.Delete);
	        var add = differences.Single(d => d.ObjectName == "Orders" && d.DifferenceType == DifferenceType.Add);
	
	        Assert.Equal("CREATE TABLE dbo.Users (Id INT); -- old", modify.DatabaseDefinition);
	        Assert.Equal("CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(50)); -- new", modify.FileDefinition);
	        Assert.Equal("CREATE TABLE dbo.Customers (Id INT);", delete.DatabaseDefinition);
	        Assert.Null(delete.FileDefinition);
	        Assert.Null(add.DatabaseDefinition);
	        Assert.Equal("CREATE TABLE dbo.Orders (Id INT);", add.FileDefinition);
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
	
	    [Fact]
	    public async Task CompareAsync_Excludes_Unsupported_Object_Types()
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
	                    DefinitionHash = "hash-users-db",
	                    DefinitionScript = "CREATE TABLE dbo.Users (Id INT);"
	                },
	                new SchemaObjectSummary
	                {
	                    SchemaName = "master",
	                    ObjectName = "AppLogin",
	                    ObjectType = SqlObjectType.Login,
	                    DefinitionHash = string.Empty,
	                    DefinitionScript = string.Empty
	                },
	                new SchemaObjectSummary
	                {
	                    SchemaName = "security",
	                    ObjectName = "ReportingRole",
	                    ObjectType = SqlObjectType.Role,
	                    DefinitionHash = "hash-role-db",
	                    DefinitionScript = "CREATE ROLE [ReportingRole];"
	                }
	            }
	        };
	
	        var cache = new FileModelCache
	        {
	            SubscriptionId = subscriptionId,
	            FileEntries =
	            {
	                ["dbo.Users.sql"] = new FileObjectEntry
	                {
	                    FilePath = "dbo/Tables/Users.sql",
	                    ObjectName = "Users",
	                    ObjectType = SqlObjectType.Table,
	                    ContentHash = "hash-users-file",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(50));"
	                },
	                ["Security/Login.sql"] = new FileObjectEntry
	                {
	                    FilePath = "Security/Login.sql",
	                    ObjectName = "AppLogin",
	                    ObjectType = SqlObjectType.Login,
	                    ContentHash = "hash-login-file",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE LOGIN [AppLogin] WITH PASSWORD = 'x';"
	                },
	                ["Misc/Unknown.sql"] = new FileObjectEntry
	                {
	                    FilePath = "Misc/Unknown.sql",
	                    ObjectName = "SomeArtifact",
	                    ObjectType = SqlObjectType.Unknown,
	                    ContentHash = "hash-unknown-file",
	                    LastModified = DateTime.UtcNow,
	                    Content = "-- some unsupported object type"
	                }
	            }
	        };
	
	        var options = new ComparisonOptions();
	
	        // Act
	        var differences = await comparer.CompareAsync(snapshot, cache, options);
	
	        // Assert
	        // Only supported types (table + role) should participate in the diff; login
	        // and unknown objects must be completely ignored.
	        Assert.All(differences, d => Assert.NotEqual(SqlObjectType.Login, d.ObjectType));
	        Assert.All(differences, d => Assert.NotEqual(SqlObjectType.Unknown, d.ObjectType));
	
	        Assert.Contains(differences, d => d.ObjectType == SqlObjectType.Table && d.ObjectName == "Users");
	        Assert.Contains(differences, d => d.ObjectType == SqlObjectType.Role && d.ObjectName == "ReportingRole");
	    }
	
	    [Fact]
	    public async Task CompareAsync_Handles_SecurityPrincipals_User_And_Role()
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
	                    ObjectName = "ReportingRole",
	                    ObjectType = SqlObjectType.Role,
	                    DefinitionHash = "hash-role-db",
	                    DefinitionScript = "CREATE ROLE [ReportingRole];"
	                },
	                new SchemaObjectSummary
	                {
	                    SchemaName = "dbo",
	                    ObjectName = "AppUser",
	                    ObjectType = SqlObjectType.User,
	                    DefinitionHash = "hash-user-db",
	                    DefinitionScript = "CREATE USER [AppUser] FOR LOGIN [AppUser];"
	                }
	            }
	        };

	        var cache = new FileModelCache
	        {
	            SubscriptionId = subscriptionId,
	            FileEntries =
	            {
	                ["ReportingRole.sql"] = new FileObjectEntry
	                {
	                    FilePath = "Security/ReportingRole.sql",
	                    ObjectName = "ReportingRole",
	                    ObjectType = SqlObjectType.Role,
	                    ContentHash = "hash-role-file",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE ROLE [ReportingRole] AUTHORIZATION [dbo];"
	                },
	                ["NewUser.sql"] = new FileObjectEntry
	                {
	                    FilePath = "Security/NewUser.sql",
	                    ObjectName = "NewUser",
	                    ObjectType = SqlObjectType.User,
	                    ContentHash = "hash-new-user",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE USER [NewUser] FOR LOGIN [NewUser];"
	                }
	            }
	        };

	        var options = new ComparisonOptions();

	        // Act
	        var differences = await comparer.CompareAsync(snapshot, cache, options);

	        // Assert
	        // Role that exists on both sides but has different definitions should be a Modify
	        var roleDiff = Assert.Single(differences.Where(d => d.ObjectType == SqlObjectType.Role && d.ObjectName == "ReportingRole"));
	        Assert.Equal(DifferenceType.Modify, roleDiff.DifferenceType);
	        Assert.Equal("CREATE ROLE [ReportingRole];", roleDiff.DatabaseDefinition);
	        Assert.Equal("CREATE ROLE [ReportingRole] AUTHORIZATION [dbo];", roleDiff.FileDefinition);

	        // User that exists only in the database should be a Delete
	        var delete = Assert.Single(differences.Where(d => d.ObjectType == SqlObjectType.User && d.ObjectName == "AppUser"));
	        Assert.Equal(DifferenceType.Delete, delete.DifferenceType);
	        Assert.Equal("CREATE USER [AppUser] FOR LOGIN [AppUser];", delete.DatabaseDefinition);
	        Assert.Null(delete.FileDefinition);

	        // User that exists only in files should be an Add
	        var add = Assert.Single(differences.Where(d => d.ObjectType == SqlObjectType.User && d.ObjectName == "NewUser"));
	        Assert.Equal(DifferenceType.Add, add.DifferenceType);
	        Assert.Null(add.DatabaseDefinition);
	        Assert.Equal("CREATE USER [NewUser] FOR LOGIN [NewUser];", add.FileDefinition);
	    }
}

