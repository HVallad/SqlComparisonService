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

    [Fact]
    public async Task CompareAsync_Handles_Multiple_Database_Objects_With_Same_Name_Across_Schemas()
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
                        ObjectName = "SharedTable",
                        ObjectType = SqlObjectType.Table,
                        DefinitionHash = "hash-dbo",
                        DefinitionScript = "CREATE TABLE dbo.SharedTable (Id INT);"
                    },
                    new SchemaObjectSummary
                    {
                        SchemaName = "ArchiveSchema",
                        ObjectName = "SharedTable",
                        ObjectType = SqlObjectType.Table,
                        DefinitionHash = "hash-archive",
                        DefinitionScript = "CREATE TABLE ArchiveSchema.SharedTable (Id INT);"
                    }
                }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                {
                    ["SharedTable.sql"] = new FileObjectEntry
                    {
                        FilePath = "SharedTable.sql",
                        ObjectName = "SharedTable",
                        ObjectType = SqlObjectType.Table,
                        ContentHash = "hash-dbo",
                        LastModified = DateTime.UtcNow,
                        Content = "CREATE TABLE dbo.SharedTable (Id INT);"
                    }
                }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        // We expect no modify for the dbo table (hash matches file), and a
        // single delete for the ArchiveSchema table that has no corresponding
        // file entry.
        var delete = Assert.Single(differences);
        Assert.Equal(DifferenceType.Delete, delete.DifferenceType);
        Assert.Equal("SharedTable", delete.ObjectName);
        Assert.Equal("ArchiveSchema", delete.SchemaName);
        Assert.Equal(SqlObjectType.Table, delete.ObjectType);
        Assert.Equal("CREATE TABLE ArchiveSchema.SharedTable (Id INT);", delete.DatabaseDefinition);
        Assert.Null(delete.FileDefinition);
    }

    [Fact]
    public async Task CompareAsync_MultiSchema_Uses_FilePath_To_Select_Primary_Db_Object()
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
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            DefinitionHash = "hash-dbo",
                            DefinitionScript = "CREATE TABLE [dbo].[SharedTable] (Id INT);"
                        },
                        new SchemaObjectSummary
                        {
                            SchemaName = "ArchiveSchema",
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            DefinitionHash = "hash-archive",
                            DefinitionScript = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                        }
                    }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                    {
                        ["ArchiveSchema/SharedTable.sql"] = new FileObjectEntry
                        {
                            FilePath = "ArchiveSchema/SharedTable.sql",
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            ContentHash = "hash-archive",
                            LastModified = DateTime.UtcNow,
                            Content = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                        }
                    }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        // The file clearly targets the ArchiveSchema schema (both by path and
        // content), so the primary match should be the ArchiveSchema table.
        // We therefore expect a single delete for the dbo table only.
        var delete = Assert.Single(differences);
        Assert.Equal(DifferenceType.Delete, delete.DifferenceType);
        Assert.Equal("SharedTable", delete.ObjectName);
        Assert.Equal("dbo", delete.SchemaName);
        Assert.Equal(SqlObjectType.Table, delete.ObjectType);
        Assert.Equal("CREATE TABLE [dbo].[SharedTable] (Id INT);", delete.DatabaseDefinition);
        Assert.Null(delete.FileDefinition);
    }

    [Fact]
    public async Task CompareAsync_MultiSchema_Uses_FileContent_To_Select_Primary_Db_Object_When_Path_Is_Ambiguous()
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
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            DefinitionHash = "hash-dbo",
                            DefinitionScript = "CREATE TABLE [dbo].[SharedTable] (Id INT);"
                        },
                        new SchemaObjectSummary
                        {
                            SchemaName = "ArchiveSchema",
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            DefinitionHash = "hash-archive",
                            DefinitionScript = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                        }
                    }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                    {
                        ["SharedTable.sql"] = new FileObjectEntry
                        {
                            FilePath = "SharedTable.sql",
                            ObjectName = "SharedTable",
                            ObjectType = SqlObjectType.Table,
                            ContentHash = "hash-archive",
                            LastModified = DateTime.UtcNow,
                            Content = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                        }
                    }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        // With no schema hint in the path, we still expect the content to
        // drive us to use the ArchiveSchema table as the primary match and
        // report a single delete for the dbo table.
        var delete = Assert.Single(differences);
        Assert.Equal(DifferenceType.Delete, delete.DifferenceType);
        Assert.Equal("SharedTable", delete.ObjectName);
        Assert.Equal("dbo", delete.SchemaName);
        Assert.Equal(SqlObjectType.Table, delete.ObjectType);
        Assert.Equal("CREATE TABLE [dbo].[SharedTable] (Id INT);", delete.DatabaseDefinition);
        Assert.Null(delete.FileDefinition);
    }

    [Fact]
    public async Task CompareAsync_MultiSchema_Multiple_Files_All_Database_Objects_Matched_By_Schema()
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
                                SchemaName = "StagingSchema",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                DefinitionHash = "hash-staging",
                                DefinitionScript = "CREATE TABLE [StagingSchema].[SharedTable] (Id INT);"
                            },
                            new SchemaObjectSummary
                            {
                                SchemaName = "ArchiveSchema",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                DefinitionHash = "hash-archive",
                                DefinitionScript = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                            }
                        }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                        {
                            ["StagingSchema/Tables/SharedTable.sql"] = new FileObjectEntry
                            {
                                FilePath = "StagingSchema/Tables/SharedTable.sql",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                ContentHash = "hash-staging",
                                LastModified = DateTime.UtcNow,
                                Content = "CREATE TABLE [StagingSchema].[SharedTable] (Id INT);"
                            },
                            ["ArchiveSchema/Tables/SharedTable.sql"] = new FileObjectEntry
                            {
                                FilePath = "ArchiveSchema/Tables/SharedTable.sql",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                ContentHash = "hash-archive",
                                LastModified = DateTime.UtcNow,
                                Content = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                            }
                        }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        // Both database objects have corresponding files that clearly target
        // their schemas by path and content, so there should be no
        // differences.
        Assert.Empty(differences);
    }

    [Fact]
    public async Task CompareAsync_MultiSchema_Extra_Database_Object_With_No_File_Is_Reported_As_Delete()
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
                                SchemaName = "StagingSchema",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                DefinitionHash = "hash-staging",
                                DefinitionScript = "CREATE TABLE [StagingSchema].[SharedTable] (Id INT);"
                            },
                            new SchemaObjectSummary
                            {
                                SchemaName = "ArchiveSchema",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                DefinitionHash = "hash-archive",
                                DefinitionScript = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                            },
                            new SchemaObjectSummary
                            {
                                SchemaName = "dbo",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                DefinitionHash = "hash-dbo",
                                DefinitionScript = "CREATE TABLE [dbo].[SharedTable] (Id INT);"
                            }
                        }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                        {
                            ["StagingSchema/Tables/SharedTable.sql"] = new FileObjectEntry
                            {
                                FilePath = "StagingSchema/Tables/SharedTable.sql",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                ContentHash = "hash-staging",
                                LastModified = DateTime.UtcNow,
                                Content = "CREATE TABLE [StagingSchema].[SharedTable] (Id INT);"
                            },
                            ["ArchiveSchema/Tables/SharedTable.sql"] = new FileObjectEntry
                            {
                                FilePath = "ArchiveSchema/Tables/SharedTable.sql",
                                ObjectName = "SharedTable",
                                ObjectType = SqlObjectType.Table,
                                ContentHash = "hash-archive",
                                LastModified = DateTime.UtcNow,
                                Content = "CREATE TABLE [ArchiveSchema].[SharedTable] (Id INT);"
                            }
                        }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        // The StagingSchema and ArchiveSchema tables both have matching files, so the
        // only difference we expect is a delete for the extra dbo table that
        // has no corresponding file.
        var delete = Assert.Single(differences);
        Assert.Equal(DifferenceType.Delete, delete.DifferenceType);
        Assert.Equal("SharedTable", delete.ObjectName);
        Assert.Equal("dbo", delete.SchemaName);
        Assert.Equal(SqlObjectType.Table, delete.ObjectType);
        Assert.Equal("CREATE TABLE [dbo].[SharedTable] (Id INT);", delete.DatabaseDefinition);
        Assert.Null(delete.FileDefinition);
    }

    [Fact]
    public async Task CompareAsync_Includes_Index_Objects_When_Table_Comparison_Enabled()
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
                            SchemaName = "SampleSchema",
                            ObjectName = "AuditLog.IX_AuditLog",
                            ObjectType = SqlObjectType.Index,
                            DefinitionHash = "hash-index-db",
                            DefinitionScript = "CREATE CLUSTERED INDEX [IX_AuditLog] ON [SampleSchema].[AuditLog]([Month] ASC);"
                        }
                    }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                    {
                        ["SampleSchema.AuditLog.sql::INDEX::AuditLog.IX_AuditLog"] = new FileObjectEntry
                        {
                            FilePath = "SampleSchema.AuditLog.sql",
                            ObjectName = "AuditLog.IX_AuditLog",
                            ObjectType = SqlObjectType.Index,
                            ContentHash = "hash-index-file",
                            LastModified = DateTime.UtcNow,
                            Content = "CREATE CLUSTERED INDEX [IX_AuditLog] ON [SampleSchema].[AuditLog]([Month] DESC);"
                        }
                    }
        };

        var options = new ComparisonOptions(); // IncludeTables defaults to true

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        var diff = Assert.Single(differences);
        Assert.Equal(SqlObjectType.Index, diff.ObjectType);
        Assert.Equal("AuditLog.IX_AuditLog", diff.ObjectName);
        Assert.Equal(DifferenceType.Modify, diff.DifferenceType);
        Assert.Equal("SampleSchema", diff.SchemaName);
        Assert.Equal("CREATE CLUSTERED INDEX [IX_AuditLog] ON [SampleSchema].[AuditLog]([Month] ASC);", diff.DatabaseDefinition);
        Assert.Equal("CREATE CLUSTERED INDEX [IX_AuditLog] ON [SampleSchema].[AuditLog]([Month] DESC);", diff.FileDefinition);
    }

    [Fact]
    public async Task CompareAsync_Handles_Multiple_Tables_With_Same_Index_Name()
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
                            ObjectName = "Table1.IX_Primary",
                            ObjectType = SqlObjectType.Index,
                            DefinitionHash = "hash-table1-db",
                            DefinitionScript = "CREATE INDEX [IX_Primary] ON [dbo].[Table1]([Id]);"
                        },
                        new SchemaObjectSummary
                        {
                            SchemaName = "dbo",
                            ObjectName = "Table2.IX_Primary",
                            ObjectType = SqlObjectType.Index,
                            DefinitionHash = "hash-table2-db",
                            DefinitionScript = "CREATE INDEX [IX_Primary] ON [dbo].[Table2]([Id]);"
                        }
                    }
        };

        var cache = new FileModelCache
        {
            SubscriptionId = subscriptionId,
            FileEntries =
                    {
                        ["Table1.sql::INDEX::Table1.IX_Primary"] = new FileObjectEntry
                        {
                            FilePath = "Table1.sql",
                            ObjectName = "Table1.IX_Primary",
                            ObjectType = SqlObjectType.Index,
                            ContentHash = "hash-table1-db",
                            LastModified = DateTime.UtcNow,
                            Content = "CREATE INDEX [IX_Primary] ON [dbo].[Table1]([Id]);"
                        },
                        ["Table2.sql::INDEX::Table2.IX_Primary"] = new FileObjectEntry
                        {
                            FilePath = "Table2.sql",
                            ObjectName = "Table2.IX_Primary",
                            ObjectType = SqlObjectType.Index,
                            ContentHash = "hash-table2-file",
                            LastModified = DateTime.UtcNow,
                            Content = "CREATE INDEX [IX_Primary] ON [dbo].[Table2]([Id] DESC);"
                        }
                    }
        };

        var options = new ComparisonOptions();

        // Act
        var differences = await comparer.CompareAsync(snapshot, cache, options);

        // Assert
        var indexDiff = Assert.Single(differences);
        Assert.Equal(SqlObjectType.Index, indexDiff.ObjectType);
        Assert.Equal("Table2.IX_Primary", indexDiff.ObjectName);
        Assert.Equal(DifferenceType.Modify, indexDiff.DifferenceType);
        Assert.Equal("dbo", indexDiff.SchemaName);
        Assert.Equal("CREATE INDEX [IX_Primary] ON [dbo].[Table2]([Id]);", indexDiff.DatabaseDefinition);
        Assert.Equal("CREATE INDEX [IX_Primary] ON [dbo].[Table2]([Id] DESC);", indexDiff.FileDefinition);
    }

	    [Fact]
	    public async Task CompareAsync_Treats_Inline_And_TableValued_Function_As_Same_Object_When_Definitions_Match()
	    {
	        // Arrange - database sees an inline table-valued function (IF) while the
	        // file model classifies it as a TableValuedFunction. When the hashes
	        // match, we should see no differences.
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
	                    ObjectName = "fn_GetDashboardInfo",
	                    ObjectType = SqlObjectType.InlineTableValuedFunction,
	                    DefinitionHash = "hash-fn",
	                    DefinitionScript = "CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS Value);"
	                }
	            }
	        };

	        var cache = new FileModelCache
	        {
	            SubscriptionId = subscriptionId,
	            FileEntries =
	            {
	                ["dbo/Functions/fn_GetDashboardInfo.sql"] = new FileObjectEntry
	                {
	                    FilePath = "dbo/Functions/fn_GetDashboardInfo.sql",
	                    ObjectName = "fn_GetDashboardInfo",
	                    ObjectType = SqlObjectType.TableValuedFunction,
	                    ContentHash = "hash-fn",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS Value);"
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
	    public async Task CompareAsync_Reports_Single_Modify_For_Function_When_Types_Differ_But_Names_Match()
	    {
	        // Arrange - same schema and function name, but the database reports an
	        // InlineTableValuedFunction while the file model reports a
	        // TableValuedFunction and the definitions differ. We should surface a
	        // single Modify difference, not separate Add/Delete entries.
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
	                    ObjectName = "fn_GetDashboardInfo",
	                    ObjectType = SqlObjectType.InlineTableValuedFunction,
	                    DefinitionHash = "hash-db",
	                    DefinitionScript = "CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS DbValue);"
	                }
	            }
	        };

	        var cache = new FileModelCache
	        {
	            SubscriptionId = subscriptionId,
	            FileEntries =
	            {
	                ["dbo/Functions/fn_GetDashboardInfo.sql"] = new FileObjectEntry
	                {
	                    FilePath = "dbo/Functions/fn_GetDashboardInfo.sql",
	                    ObjectName = "fn_GetDashboardInfo",
	                    ObjectType = SqlObjectType.TableValuedFunction,
	                    ContentHash = "hash-file",
	                    LastModified = DateTime.UtcNow,
	                    Content = "CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS FileValue);"
	                }
	            }
	        };

	        var options = new ComparisonOptions();

	        // Act
	        var differences = await comparer.CompareAsync(snapshot, cache, options);

	        // Assert
	        var diff = Assert.Single(differences);
	        Assert.Equal(DifferenceType.Modify, diff.DifferenceType);
	        Assert.Equal("fn_GetDashboardInfo", diff.ObjectName);
	        Assert.Equal("dbo", diff.SchemaName);
	        Assert.Equal(SqlObjectType.InlineTableValuedFunction, diff.ObjectType);
	        Assert.Equal("CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS DbValue);", diff.DatabaseDefinition);
	        Assert.Equal("CREATE FUNCTION [dbo].[fn_GetDashboardInfo]() RETURNS TABLE AS RETURN (SELECT 1 AS FileValue);", diff.FileDefinition);
	    }
}

