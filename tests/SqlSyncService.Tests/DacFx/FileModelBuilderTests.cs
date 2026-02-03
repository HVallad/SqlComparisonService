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
    public async Task BuildCacheAsync_For_Temporal_Table_Normalizes_Trailing_Comma_Before_Period_For_System_Time()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "temporal_trailing_comma_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "SampleSchema.OrderQueue.sql");
            var script = @"CREATE TABLE [SampleSchema].[OrderQueue] (
	    [Id]                      INT                                         IDENTITY (1, 1) NOT NULL,
	    [ScheduledTime]           DATETIME2 (7)                               NOT NULL,
	    [OrderNumber]             NVARCHAR (50)                               NOT NULL,
	    [ReferenceNumber]         NVARCHAR (50)                               NOT NULL,
	    [Priority]                INT                                         NOT NULL,
	    [Category]                NVARCHAR (50)                               NOT NULL,
	    [Status]                  NVARCHAR (50)                               NOT NULL,
	    [ParentOrderId]           INT                                         NULL,
	    [SortOrder]               INT                                         NULL,
	    [ItemCode]                NVARCHAR (50)                               NULL,
	    [CreatedDate]             DATETIME2 (7)                               NOT NULL,
	    [ValidFrom]               DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
	    [ValidTo]                 DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
	    [DetailId]                INT                                         NULL,
	    [TrackingNumber]          NVARCHAR (50)                               NULL,
	    [VendorNumber]            NVARCHAR (50)                               NULL,
	    [RequiredQty]             NUMERIC (18, 3)                             NULL,
	    [ProcessedQty]            NUMERIC (18, 3)                             NULL,
	    [GroupName]               NVARCHAR (50)                               NULL,
	    [ItemStatusID]            INT                                         NULL,
	    [OrderTypeID]             INT                                         NULL,
	    [ProcessedDate]           DATETIME                                    NULL,
	    [VerifiedQty]             INT                                         NULL,
	    [ProcessedBy]             INT                                         NULL,
	    [OrderID]                 INT                                         NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
);";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var entry = cache.FileEntries["SampleSchema.OrderQueue.sql"];

            // Assert - trailing comma on the last column before PERIOD FOR SYSTEM_TIME
            // should be normalized away in the stored content.
            Assert.Equal(SqlObjectType.Table, entry.ObjectType);

            var databaseScript = script.Replace(
                "[OrderID]                 INT                                         NULL,",
                "[OrderID]                 INT                                         NULL");

            var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
            var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
            var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
            var expectedContent = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

            Assert.Equal(expectedContent, entry.Content);
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

    [Fact]
    public async Task BuildCacheAsync_Preserves_Dots_In_Table_Names_From_Create_Statement()
    {
        // Arrange - table name contains a dot (e.g., Audit.DataConversions)
        // The dot is part of the table name, not a schema separator.
        var root = Path.Combine(Path.GetTempPath(), "dotted_table_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "Audit.DataConversions.sql");
            var script = @"CREATE TABLE [SampleSchema].[Audit.DataConversions] (
    [Id]                      INT            NOT NULL,
    [InputData]               NVARCHAR (MAX) NOT NULL,
    [OutputData]              NVARCHAR (MAX) NULL,
    [ProcessedTime]           DATETIME2 (7)  NULL,
    [ProcessStatusId]         INT            NOT NULL,
    [ConfigId]                INT            NOT NULL,
    [ValidFrom]               DATETIME2 (7)  NOT NULL,
    [ValidTo]                 DATETIME2 (7)  NOT NULL,
    [CreatedBy]               INT            NOT NULL,
    [CreatedDate]             DATETIME2 (7)  NOT NULL,
    [ModifiedById]            INT            NULL,
    [ModifiedDate]            DATETIME2 (7)  NULL,
    [OutputTableId]           INT            NULL,
    [SourceName]              NVARCHAR (50)  NOT NULL
);";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
            var entry = Assert.Single(cache.FileEntries).Value;

            // Assert - The full table name including the internal dot should be preserved
            Assert.Equal(SqlObjectType.Table, entry.ObjectType);
            Assert.Equal("Audit.DataConversions", entry.ObjectName);
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
    public void TryExtractTableName_Parses_Bracketed_Table_Name_With_Dots()
    {
        // Arrange
        var sql = "CREATE TABLE [SampleSchema].[Audit.DataConfig] ([Id] INT NOT NULL);";

        // Act
        var tableName = FileModelBuilder.TryExtractTableName(sql);

        // Assert - should return only the table name, not schema, preserving internal dot
        Assert.Equal("Audit.DataConfig", tableName);
    }

    [Fact]
    public void TryExtractTableName_Parses_Simple_Bracketed_Table_Name()
    {
        // Arrange
        var sql = "CREATE TABLE [dbo].[Users] ([Id] INT NOT NULL);";

        // Act
        var tableName = FileModelBuilder.TryExtractTableName(sql);

        // Assert
        Assert.Equal("Users", tableName);
    }

    [Fact]
    public void TryExtractTableName_Parses_Unbracketed_Table_Name()
    {
        // Arrange
        var sql = "CREATE TABLE dbo.Customers (Id INT NOT NULL);";

        // Act
        var tableName = FileModelBuilder.TryExtractTableName(sql);

        // Assert
        Assert.Equal("Customers", tableName);
    }

    [Fact]
    public void TryExtractTableName_Returns_Empty_When_No_Create_Table()
    {
        // Arrange
        var sql = "CREATE VIEW dbo.MyView AS SELECT 1;";

        // Act
        var tableName = FileModelBuilder.TryExtractTableName(sql);

        // Assert
        Assert.Equal(string.Empty, tableName);
    }

    #region TryExtractViewName Tests

    [Fact]
    public void TryExtractViewName_Parses_Bracketed_View_Name()
    {
        // Arrange
        var sql = "CREATE VIEW [dbo].[vw_BMW_FileImports] AS SELECT 1;";

        // Act
        var viewName = FileModelBuilder.TryExtractViewName(sql);

        // Assert
        Assert.Equal("vw_BMW_FileImports", viewName);
    }

    [Fact]
    public void TryExtractViewName_Parses_Create_Or_Alter_View()
    {
        // Arrange
        var sql = "CREATE OR ALTER VIEW [dbo].[vw_MyView] AS SELECT 1;";

        // Act
        var viewName = FileModelBuilder.TryExtractViewName(sql);

        // Assert
        Assert.Equal("vw_MyView", viewName);
    }

    [Fact]
    public void TryExtractViewName_Parses_Alter_View()
    {
        // Arrange
        var sql = "ALTER VIEW [dbo].[vw_Modified] AS SELECT 2;";

        // Act
        var viewName = FileModelBuilder.TryExtractViewName(sql);

        // Assert
        Assert.Equal("vw_Modified", viewName);
    }

    [Fact]
    public void TryExtractViewName_Returns_Empty_When_No_Create_View()
    {
        // Arrange
        var sql = "CREATE TABLE dbo.MyTable (Id INT);";

        // Act
        var viewName = FileModelBuilder.TryExtractViewName(sql);

        // Assert
        Assert.Equal(string.Empty, viewName);
    }

    #endregion

    #region TryExtractProcedureName Tests

    [Fact]
    public void TryExtractProcedureName_Parses_Bracketed_Procedure_Name()
    {
        // Arrange
        var sql = "CREATE PROCEDURE [dbo].[sp_GetUsers] AS SELECT 1;";

        // Act
        var procName = FileModelBuilder.TryExtractProcedureName(sql);

        // Assert
        Assert.Equal("sp_GetUsers", procName);
    }

    [Fact]
    public void TryExtractProcedureName_Parses_Create_Proc_Short_Form()
    {
        // Arrange
        var sql = "CREATE PROC [dbo].[sp_Short] AS SELECT 1;";

        // Act
        var procName = FileModelBuilder.TryExtractProcedureName(sql);

        // Assert
        Assert.Equal("sp_Short", procName);
    }

    [Fact]
    public void TryExtractProcedureName_Parses_Create_Or_Alter_Procedure()
    {
        // Arrange
        var sql = "CREATE OR ALTER PROCEDURE [dbo].[sp_Updated] AS SELECT 1;";

        // Act
        var procName = FileModelBuilder.TryExtractProcedureName(sql);

        // Assert
        Assert.Equal("sp_Updated", procName);
    }

    [Fact]
    public void TryExtractProcedureName_Returns_Empty_When_No_Create_Procedure()
    {
        // Arrange
        var sql = "CREATE VIEW dbo.MyView AS SELECT 1;";

        // Act
        var procName = FileModelBuilder.TryExtractProcedureName(sql);

        // Assert
        Assert.Equal(string.Empty, procName);
    }

    [Fact]
    public void TryExtractProcedureName_Handles_Extra_Spaces_Between_Create_And_Procedure()
    {
        // Arrange - This is the actual format seen in production SQL files
        var sql = "CREATE   procedure [SampleSchema].[SyncDataItems]\nas\nSELECT 1;";

        // Act
        var procName = FileModelBuilder.TryExtractProcedureName(sql);

        // Assert
        Assert.Equal("SyncDataItems", procName);
    }

    #endregion

    #region TryExtractFunctionName Tests

    [Fact]
    public void TryExtractFunctionName_Parses_Bracketed_Function_Name()
    {
        // Arrange
        var sql = "CREATE FUNCTION [dbo].[fn_GetValue]() RETURNS INT AS BEGIN RETURN 1; END;";

        // Act
        var funcName = FileModelBuilder.TryExtractFunctionName(sql);

        // Assert
        Assert.Equal("fn_GetValue", funcName);
    }

    [Fact]
    public void TryExtractFunctionName_Parses_Create_Or_Alter_Function()
    {
        // Arrange
        var sql = "CREATE OR ALTER FUNCTION [dbo].[fn_Updated]() RETURNS INT AS BEGIN RETURN 1; END;";

        // Act
        var funcName = FileModelBuilder.TryExtractFunctionName(sql);

        // Assert
        Assert.Equal("fn_Updated", funcName);
    }

    [Fact]
    public void TryExtractFunctionName_Returns_Empty_When_No_Create_Function()
    {
        // Arrange
        var sql = "CREATE TABLE dbo.MyTable (Id INT);";

        // Act
        var funcName = FileModelBuilder.TryExtractFunctionName(sql);

        // Assert
        Assert.Equal(string.Empty, funcName);
    }

    #endregion

    #region TryExtractTriggerName Tests

    [Fact]
    public void TryExtractTriggerName_Parses_Bracketed_Trigger_Name()
    {
        // Arrange
        var sql = "CREATE TRIGGER [dbo].[tr_AfterInsert] ON [dbo].[Users] AFTER INSERT AS BEGIN END;";

        // Act
        var triggerName = FileModelBuilder.TryExtractTriggerName(sql);

        // Assert
        Assert.Equal("tr_AfterInsert", triggerName);
    }

    [Fact]
    public void TryExtractTriggerName_Parses_Create_Or_Alter_Trigger()
    {
        // Arrange
        var sql = "CREATE OR ALTER TRIGGER [dbo].[tr_Updated] ON [dbo].[Users] AFTER UPDATE AS BEGIN END;";

        // Act
        var triggerName = FileModelBuilder.TryExtractTriggerName(sql);

        // Assert
        Assert.Equal("tr_Updated", triggerName);
    }

    [Fact]
    public void TryExtractTriggerName_Returns_Empty_When_No_Create_Trigger()
    {
        // Arrange
        var sql = "CREATE TABLE dbo.MyTable (Id INT);";

        // Act
        var triggerName = FileModelBuilder.TryExtractTriggerName(sql);

        // Assert
        Assert.Equal(string.Empty, triggerName);
    }

    #endregion

    #region InferObjectType Function Type Tests

    [Fact]
    public void InferObjectType_Classifies_Inline_Table_Valued_Function_As_TableValuedFunction()
    {
        // Arrange - This is an inline table-valued function with RETURNS TABLE
        var sql = @"CREATE FUNCTION [dbo].[fn_GetDashboardInfo]
(
    @locationID INT,
    @dashboardID INT
)
RETURNS TABLE
AS
RETURN
(
    SELECT Id, Name FROM Dashboard WHERE DashboardID = @dashboardID
)";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert - DacFx classifies this as TableValuedFunction, so we should too
        Assert.Equal(SqlObjectType.TableValuedFunction, objectType);
    }

    [Fact]
    public void InferObjectType_Classifies_Multi_Statement_Table_Valued_Function_As_TableValuedFunction()
    {
        // Arrange - This is a multi-statement table-valued function with RETURNS @table TABLE
        var sql = @"CREATE FUNCTION [dbo].[fn_GetEmployees](@DeptId INT)
RETURNS @result TABLE
(
    EmployeeId INT,
    EmployeeName NVARCHAR(100)
)
AS
BEGIN
    INSERT INTO @result SELECT Id, Name FROM Employees WHERE DeptId = @DeptId;
    RETURN;
END";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert
        Assert.Equal(SqlObjectType.TableValuedFunction, objectType);
    }

    [Fact]
    public void InferObjectType_Classifies_Scalar_Function_As_ScalarFunction()
    {
        // Arrange - This is a scalar function with RETURNS INT
        var sql = @"CREATE FUNCTION [dbo].[fn_GetValue]()
RETURNS INT
AS
BEGIN
    RETURN 1;
END";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert
        Assert.Equal(SqlObjectType.ScalarFunction, objectType);
    }

    [Fact]
    public void InferObjectType_Classifies_Scalar_Function_With_Varchar_Return_As_ScalarFunction()
    {
        // Arrange - This is a scalar function with RETURNS VARCHAR
        var sql = @"CREATE FUNCTION [dbo].[fn_FormatName](@FirstName VARCHAR(50), @LastName VARCHAR(50))
RETURNS VARCHAR(100)
AS
BEGIN
    RETURN @FirstName + ' ' + @LastName;
END";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert
        Assert.Equal(SqlObjectType.ScalarFunction, objectType);
    }

    [Fact]
    public void InferObjectType_Handles_Inline_TVF_With_CTE()
    {
        // Arrange - Real-world example with CTE and RETURNS TABLE
        var sql = @"CREATE FUNCTION [dbo].[GenerateIncrementingRecords](@RecordCount INT)
RETURNS TABLE
AS
RETURN
WITH CTE AS
(
    SELECT 1 as RecordID
    UNION ALL
    SELECT RecordID + 1
    FROM CTE
    WHERE RecordID < @RecordCount
)
SELECT *
FROM CTE";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert
        Assert.Equal(SqlObjectType.TableValuedFunction, objectType);
    }

    [Fact]
    public void InferObjectType_Handles_Function_With_Comments()
    {
        // Arrange - Function with comments before CREATE FUNCTION
        var sql = @"-- =============================================
-- Author:		<Author,,Name>
-- Create date: <Create Date,,>
-- Description:	<Description,,>
-- =============================================
CREATE FUNCTION [dbo].[fn_GetDashboardInfo]
(
    @locationID INT
)
RETURNS TABLE
AS
RETURN
(
    SELECT Id, Name FROM Dashboard
)";

        // Act
        var objectType = FileModelBuilder.InferObjectType(sql);

        // Assert
        Assert.Equal(SqlObjectType.TableValuedFunction, objectType);
    }

    #endregion
}