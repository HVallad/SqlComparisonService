using SqlSyncService.DacFx;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Tests.DacFx;

public class FileModelBuilderTests
{
    [Fact]
    public async Task BuildCacheAsync_Classifies_Function_With_CreateTable_In_Body_As_Function()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "func_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "dbo.fn_GenerateCreateTableScript.sql");
            var script = @"CREATE FUNCTION [dbo].[fn_GenerateCreateTableScript]()
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX) = 'CREATE TABLE dbo.Test(Id int);';
    RETURN @sql;
END";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal("fn_GenerateCreateTableScript", entry.ObjectName);
            Assert.Equal(SqlObjectType.ScalarFunction, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Extracts_Index_From_Table_File_As_Separate_Object()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "table_index_extract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "SampleSchema.AuditLog.sql");
            var script = @"CREATE TABLE [SampleSchema].[AuditLog] (
	    [Id]          INT           NOT NULL,
	    [Month]       INT           NOT NULL
	);

	GO

	CREATE CLUSTERED INDEX [IX_AuditLog]
	    ON [SampleSchema].[AuditLog]([Month] ASC);";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            Assert.Equal(2, cache.FileEntries.Count);
            var tableEntry = cache.FileEntries.Values.Single(e => e.ObjectType == SqlObjectType.Table);
            var indexEntry = cache.FileEntries.Values.Single(e => e.ObjectType == SqlObjectType.Index);

            Assert.Equal("AuditLog", tableEntry.ObjectName);
            Assert.Equal("AuditLog.IX_AuditLog", indexEntry.ObjectName);
            Assert.Contains("CREATE CLUSTERED INDEX", indexEntry.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_For_Table_Ignores_Ddl_After_First_Go_When_Computing_Content_And_Hash()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "table_with_index_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "SampleSchema.AuditLog.sql");
            var tableOnly = @"CREATE TABLE [SampleSchema].[AuditLog] (
		    [Id]          INT           NOT NULL,
		    [Month]       INT           NOT NULL
		);";

            var scriptWithIndex1 = tableOnly + @"
			
			GO
			
			CREATE CLUSTERED INDEX [IX_AuditLog]
			    ON [SampleSchema].[AuditLog]([Month] ASC);";

            var scriptWithIndex2 = tableOnly + @"
			
			GO
			
			CREATE CLUSTERED INDEX [IX_AuditLog]
			    ON [SampleSchema].[AuditLog]([Id] ASC);";

            await File.WriteAllTextAsync(sqlPath, scriptWithIndex1);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act - first build
            var cache1 = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var entry1 = cache1.FileEntries.Values.Single(e => e.ObjectType == SqlObjectType.Table);

            // Modify only the DDL after GO and rebuild
            await File.WriteAllTextAsync(sqlPath, scriptWithIndex2);
            var cache2 = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var entry2 = cache2.FileEntries.Values.Single(e => e.ObjectType == SqlObjectType.Table);

            // Assert
            Assert.Equal(SqlObjectType.Table, entry1.ObjectType);
            Assert.Equal(SqlObjectType.Table, entry2.ObjectType);
            Assert.DoesNotContain("CREATE CLUSTERED INDEX", entry1.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GO", entry1.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(entry1.Content, entry2.Content); // table batch only
            Assert.Equal(entry1.ContentHash, entry2.ContentHash);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_For_Table_Strips_Inline_Constraints_From_Content()
	    {
	        // Arrange
	        var root = Path.Combine(Path.GetTempPath(), "table_inline_constraints_" + Guid.NewGuid().ToString("N"));
	        Directory.CreateDirectory(root);
	
	        try
	        {
	            var sqlPath = Path.Combine(root, "SampleSchema.MaterialLog.sql");
	            var script = @"CREATE TABLE [SampleSchema].[MaterialLog] (
			    [Id]                          INT              IDENTITY (1, 1) NOT NULL,
			    [ColumnA]                     INT              NOT NULL,
			    [ColumnB]                     INT              NULL,
			    [ColumnC]                     UNIQUEIDENTIFIER NULL,
			    [ColumnD]                     NVARCHAR (50)    NULL,
			    CONSTRAINT [PK_MaterialLog] PRIMARY KEY CLUSTERED ([Id] ASC),
			    CONSTRAINT [FK_MaterialLog_RefTable1] FOREIGN KEY ([ColumnC]) REFERENCES [SampleSchema].[RefTable1] ([RefId]),
			    CONSTRAINT [FK_MaterialLog_RefTable2] FOREIGN KEY ([ColumnA]) REFERENCES [SampleSchema].[RefTable2] ([Id]) ON DELETE CASCADE
			);
			";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var tableEntry = cache.FileEntries.Values.Single(e => e.ObjectType == SqlObjectType.Table);

	        // Assert - constraints are stripped from the stored content
	        Assert.DoesNotContain("CONSTRAINT", tableEntry.Content, StringComparison.OrdinalIgnoreCase);
	        Assert.DoesNotContain("PRIMARY KEY", tableEntry.Content, StringComparison.OrdinalIgnoreCase);
	        Assert.DoesNotContain("FOREIGN KEY", tableEntry.Content, StringComparison.OrdinalIgnoreCase);
	        Assert.Contains("[Id]", tableEntry.Content, StringComparison.OrdinalIgnoreCase);
	        Assert.Contains("[ColumnA]", tableEntry.Content, StringComparison.OrdinalIgnoreCase);
	    }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_For_Table_Strips_Column_Level_Inline_Default_Constraints()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "table_column_inline_defaults_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = @"CREATE TABLE [SampleSchema].[TemporalEvents] (
			    [Id]          INT                                         IDENTITY (1, 1) NOT NULL,
			    [Month]       INT                                         NOT NULL,
			    [Day]         INT                                         NOT NULL,
			    [StartTime]   TIME (0)                                    NOT NULL,
			    [EndTime]     TIME (0)                                    NULL,
			    [CreatedDate] DATETIME2 (7)                               NOT NULL,
			    [ValidFrom]   DATETIME2 (7) GENERATED ALWAYS AS ROW START CONSTRAINT [DF_TemporalEvents_ValidFrom] DEFAULT (sysutcdatetime()) NOT NULL,
			    [ValidTo]     DATETIME2 (7) GENERATED ALWAYS AS ROW END   CONSTRAINT [DF_TemporalEvents_ValidTo] DEFAULT (CONVERT([datetime2],'9999-12-31 23:59:59.9999999')) NOT NULL,
			    CONSTRAINT [PK_TemporalEvents] PRIMARY KEY CLUSTERED ([Id]),
			    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
		)
		WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE=[SampleSchema].[AuditLog], DATA_CONSISTENCY_CHECK=ON));";

            var filePath = Path.Combine(root, "SampleSchema.TemporalEvents.sql");
            await File.WriteAllTextAsync(filePath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            Assert.True(cache.FileEntries.ContainsKey("SampleSchema.TemporalEvents.sql"));
            var entry = cache.FileEntries["SampleSchema.TemporalEvents.sql"]; // table entry

            // Column-level default constraints should be stripped out.
            Assert.DoesNotContain("DF_TemporalEvents_ValidFrom", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DF_TemporalEvents_ValidTo", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DEFAULT (sysutcdatetime())", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DEFAULT (CONVERT", entry.Content, StringComparison.OrdinalIgnoreCase);

            // Core temporal column definitions and PERIOD clause should remain.
            Assert.Contains("ValidFrom", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ValidTo", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GENERATED ALWAYS AS ROW START", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GENERATED ALWAYS AS ROW END", entry.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PERIOD FOR SYSTEM_TIME", entry.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Ignores_Comments_When_Classifying_ObjectType()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "comments_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CommentTest.sql");
            var script = @"-- CREATE TABLE dbo.ShouldBeIgnored(Id int);
/* CREATE PROCEDURE dbo.ShouldAlsoBeIgnored AS SELECT 1; */
CREATE VIEW dbo.RealView AS SELECT 42 AS Value;";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.View, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Classifies_Login_Script_As_Login()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "login_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CreateLogin.sql");
            var script = @"CREATE LOGIN [TestLogin] WITH PASSWORD = 'P@ssw0rd';";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.Login, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Classifies_Role_Script_As_Role()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "role_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CreateRole.sql");
            var script = @"CREATE ROLE [ReportingRole];";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.Role, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Classifies_User_Script_As_User()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "user_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CreateUser.sql");
            var script = @"CREATE USER [TestUser] FOR LOGIN [TestLogin];";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.User, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCacheAsync_Classifies_Unknown_When_No_Recognized_Ddl()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "unknown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "MiscScript.sql");
            var script = @"PRINT 'Hello world';";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var entry = Assert.Single(cache.FileEntries).Value;

            // Assert
            Assert.Equal(SqlObjectType.Unknown, entry.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}