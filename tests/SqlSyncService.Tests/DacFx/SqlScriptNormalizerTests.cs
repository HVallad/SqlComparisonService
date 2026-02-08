using SqlSyncService.DacFx;
using Xunit;

namespace SqlSyncService.Tests.DacFx;

public class SqlScriptNormalizerTests
{
    [Fact]
    public void StripInlineConstraints_Normalizes_Sample_Table_With_Defaults_Database_And_File_Scripts()
    {
        var databaseScript = @"CREATE TABLE [SampleSchema].[SampleTableWithDefaults] (
    [ExecutionID] UNIQUEIDENTIFIER CONSTRAINT [DF_SampleTableWithDefaults_ExecutionID] DEFAULT (newid()) NOT NULL,
    [ParameterID] UNIQUEIDENTIFIER CONSTRAINT [DF_SampleTableWithDefaults_ParameterID] DEFAULT (newid()) NOT NULL,
    [PartNumber] NVARCHAR (50) NULL,
    [ExecutionDate] DATETIME CONSTRAINT [DF_SampleTableWithDefaults_ExecutionDate] DEFAULT (getdate()) NULL,
    CONSTRAINT [PK_SampleTableWithDefaults] PRIMARY KEY NONCLUSTERED ([ExecutionID] ASC, [ParameterID] ASC)
)
WITH (DURABILITY = SCHEMA_ONLY, MEMORY_OPTIMIZED = ON);";

        var fileScript = @"CREATE TABLE [SampleSchema].[SampleTableWithDefaults] (
    [ExecutionID] UNIQUEIDENTIFIER NOT NULL,
    [ParameterID] UNIQUEIDENTIFIER NOT NULL,
    [PartNumber] NVARCHAR (50) NULL,
    [ExecutionDate] DATETIME NULL,
)
WITH (DURABILITY = SCHEMA_ONLY, MEMORY_OPTIMIZED = ON);";

        // Apply the same normalization pipeline that database and file
        // model builders use for table scripts.
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void NormalizeForComparison_Preserves_Differences_In_Period_For_System_Time_Clauses()
    {
        var databaseScript = @"CREATE TABLE [SampleSchema].[TemporalTableWithPeriod] (
	    [Id] INT NOT NULL,
	    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
	    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom])
);";

        var fileScript = @"CREATE TABLE [SampleSchema].[TemporalTableWithPeriod] (
	    [Id] INT NOT NULL,
	    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
	    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
);";

        // Apply the same normalization pipeline that table scripts use so that
        // we verify PERIOD FOR SYSTEM_TIME differences are not normalized away.
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.NotEqual(dbForComparison, fileForComparison);
    }

    [Fact]
    public void StripInlineConstraints_Preserves_Period_For_System_Time_Line_With_ValidFrom_And_ValidTo()
    {
        var script = @"CREATE TABLE [SampleSchema].[TemporalEventsWithoutConstraints] (
	    [Id] INT NOT NULL,
	    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
	    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
);";

        var normalized = SqlScriptNormalizer.Normalize(script);
        var firstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(normalized);
        var stripped = SqlScriptNormalizer.StripInlineConstraints(firstBatch);

        Assert.Contains("PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])", stripped);
    }

    [Fact]
    public void StripInlineConstraints_Normalizes_Trailing_Comma_Before_Period_For_System_Time()
    {
        var databaseScript = @"CREATE TABLE [SampleSchema].[TemporalTableWithTrailingComma] (
		    [Id] INT NOT NULL,
		    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
		    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
		    [OrderID] INT NULL
		    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
		);";

        var fileScript = @"CREATE TABLE [SampleSchema].[TemporalTableWithTrailingComma] (
		    [Id] INT NOT NULL,
		    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
		    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
		    [OrderID] INT NULL,
		    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
		);";

        // Apply the same normalization pipeline used by the database and
        // file model builders for table scripts so that a trailing comma on
        // the last column before PERIOD FOR SYSTEM_TIME does not cause a
        // spurious Modify difference.
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void StripInlineConstraints_Normalizes_Trailing_Comma_For_SequenceFeed_Like_Script()
    {
        var fileScript = @"CREATE TABLE [SampleSchema].[SequenceFeed] (
	    [Id]                      INT                                         IDENTITY (1, 1) NOT NULL,
	    [SWET]                    DATETIME2 (7)                               NOT NULL,
	    [OrderNumber]             NVARCHAR (50)                               NOT NULL,
	    [CallOffNumber]           NVARCHAR (50)                               NOT NULL,
	    [RunningNumber]           INT                                         NOT NULL,
	    [PartFamily]              NVARCHAR (50)                               NOT NULL,
	    [SequenceType]            NVARCHAR (50)                               NOT NULL,
	    [SequenceOrderId]         INT                                         NULL,
	    [SlotSequence]            INT                                         NULL,
	    [PartNumber]              NVARCHAR (50)                               NULL,
	    [CreatedDate]             DATETIME2 (7)                               NOT NULL,
	    [ValidFrom]               DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
	    [ValidTo]                 DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
	    [InventoryDetailSerialId] INT                                         NULL,
	    [SequenceNumber]          NVARCHAR (50)                               NULL,
	    [SupplierNumber]          NVARCHAR (50)                               NULL,
	    [RequiredQty]             NUMERIC (18, 3)                             NULL,
	    [SequencedQty]            NUMERIC (18, 3)                             NULL,
	    [SupplyGroup]             NVARCHAR (50)                               NULL,
	    [RackItemStatusID]        INT                                         NULL,
	    [SequenceOrderTypeID]     INT                                         NULL,
	    [SequencedDate]           DATETIME                                    NULL,
	    [AuditedQty]              INT                                         NULL,
	    [SequencedBy]             INT                                         NULL,
	    [OrderID]                 INT                                         NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
);";

        var databaseScript = fileScript.Replace(
            "[OrderID]                 INT                                         NULL,",
            "[OrderID]                 INT                                         NULL");

        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void StripInlineConstraints_Preserves_Numeric_Precision_And_Scale_For_Last_Column()
    {
        var script = @"CREATE TABLE [dbo].[NumericSample] (
	    [Id] INT IDENTITY (1, 1) NOT NULL,
	    [Quantity] NUMERIC (18, 2) NULL
);";

        var normalized = SqlScriptNormalizer.Normalize(script);
        var firstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(normalized);
        var stripped = SqlScriptNormalizer.StripInlineConstraints(firstBatch);
        var forComparison = SqlScriptNormalizer.NormalizeForComparison(stripped);

        // Precision and scale must be preserved even after normalization of
        // intra-line whitespace and other formatting details.
        Assert.Contains("NUMERIC(18, 2) NULL", forComparison);
    }

    [Fact]
    public void NormalizeForComparison_Treats_Datetime2_And_Datetime2_7_As_Equivalent_In_Table_Definitions()
    {
        var databaseScript = @"CREATE TABLE [BMWSequence].[Audit_HolidayHours]
	(
	    [Id] INT NOT NULL,
	    [Month] INT NOT NULL,
	    [Day] INT NOT NULL,
	    [StartTime] TIME(0) NOT NULL,
	    [EndTime] TIME(0) NULL,
	    [CreatedDate] DATETIME2 NOT NULL,
	    [ValidFrom] DATETIME2 NOT NULL,
	    [ValidTo] DATETIME2 NOT NULL
	)";

        var fileScript = @"CREATE TABLE [BMWSequence].[Audit_HolidayHours] (
	    [Id] INT NOT NULL,
	    [Month] INT NOT NULL,
	    [Day] INT NOT NULL,
	    [StartTime] TIME (0) NOT NULL,
	    [EndTime] TIME (0) NULL,
	    [CreatedDate] DATETIME2 (7) NOT NULL,
	    [ValidFrom] DATETIME2 (7) NOT NULL,
	    [ValidTo] DATETIME2 (7) NOT NULL
	);";

        // Apply the same normalization pipeline that the database and file
        // model builders use for table scripts. The two scripts differ only
        // by formatting, DATETIME2 vs DATETIME2(7), whitespace before
        // parentheses, and an optional trailing semicolon, all of which
        // should normalize away.
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void NormalizeForComparison_Treats_Numeric_Comma_Spacing_As_Insignificant_In_Table_Definitions()
    {
        var databaseScript = @"CREATE TABLE [BMWSequence].[Audit_SequenceFeed](
	    [Id] INT NOT NULL,
	    [SWET] DATETIME2(7) NOT NULL,
	    [OrderNumber] NVARCHAR(50) NOT NULL,
	    [CallOffNumber] NVARCHAR(50) NOT NULL,
	    [RunningNumber] INT NOT NULL,
	    [PartFamily] NVARCHAR(50) NOT NULL,
	    [SequenceType] NVARCHAR(50) NOT NULL,
	    [SequenceOrderId] INT NULL,
	    [SlotSequence] INT NULL,
	    [PartNumber] NVARCHAR(50) NULL,
	    [CreatedDate] DATETIME2(7) NOT NULL,
	    [ValidFrom] DATETIME2(7) NOT NULL,
	    [ValidTo] DATETIME2(7) NOT NULL,
	    [InventoryDetailSerialId] INT NULL,
	    [SequenceNumber] NVARCHAR(50) NULL,
	    [SupplierNumber] NVARCHAR(50) NULL,
	    [RequiredQty] NUMERIC(18,3) NULL,
	    [SequencedQty] NUMERIC(18,3) NULL,
	    [SupplyGroup] NVARCHAR(50) NULL,
	    [RackItemStatusID] INT NULL
);";

        var fileScript = @"CREATE TABLE [BMWSequence].[Audit_SequenceFeed](
	    [Id] INT NOT NULL,
	    [SWET] DATETIME2(7) NOT NULL,
	    [OrderNumber] NVARCHAR(50) NOT NULL,
	    [CallOffNumber] NVARCHAR(50) NOT NULL,
	    [RunningNumber] INT NOT NULL,
	    [PartFamily] NVARCHAR(50) NOT NULL,
	    [SequenceType] NVARCHAR(50) NOT NULL,
	    [SequenceOrderId] INT NULL,
	    [SlotSequence] INT NULL,
	    [PartNumber] NVARCHAR(50) NULL,
	    [CreatedDate] DATETIME2(7) NOT NULL,
	    [ValidFrom] DATETIME2(7) NOT NULL,
	    [ValidTo] DATETIME2(7) NOT NULL,
	    [InventoryDetailSerialId] INT NULL,
	    [SequenceNumber] NVARCHAR(50) NULL,
	    [SupplierNumber] NVARCHAR(50) NULL,
	    [RequiredQty] NUMERIC(18, 3) NULL,
	    [SequencedQty] NUMERIC(18, 3) NULL,
	    [SupplyGroup] NVARCHAR(50) NULL,
	    [RackItemStatusID] INT NULL
);";

        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void NormalizeForComparison_Treats_Identity_Comma_Spacing_As_Insignificant_In_Table_Definitions()
    {
        var databaseScript = @"CREATE TABLE [BMWSequence].[OutboundRackMasterInboundPartFamilies](
	    [ID] INT IDENTITY(100,1) NOT NULL,
	    [OutboundRackMasterID] INT NOT NULL,
	    [InboundPartFamily] NVARCHAR(50) NULL
);";

        var fileScript = @"CREATE TABLE [BMWSequence].[OutboundRackMasterInboundPartFamilies](
	    [ID] INT IDENTITY(100, 1) NOT NULL,
	    [OutboundRackMasterID] INT NOT NULL,
	    [InboundPartFamily] NVARCHAR(50) NULL
);";

        var dbNormalized2 = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch2 = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized2);
        var dbStripped2 = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch2);
        var dbForComparison2 = SqlScriptNormalizer.NormalizeForComparison(dbStripped2);

        var fileNormalized2 = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch2 = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized2);
        var fileStripped2 = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch2);
        var fileForComparison2 = SqlScriptNormalizer.NormalizeForComparison(fileStripped2);

        Assert.Equal(dbForComparison2, fileForComparison2);
    }

    [Fact]
    public void NormalizeForComparison_Temporal_Table_With_All_Clauses_Matches_File_Script()
    {
        // This test verifies that a temporal table script generated by TableScriptBuilder
        // (which now includes GENERATED ALWAYS AS ROW START/END, PERIOD FOR SYSTEM_TIME,
        // and WITH(SYSTEM_VERSIONING = ON ... DATA_CONSISTENCY_CHECK = ON)) matches a file script after normalization.
        var databaseScript = @"CREATE TABLE [BMWSequence].[HolidayHours]
(
    [Id] INT IDENTITY(1,1) NOT NULL,
    [Month] INT NOT NULL,
    [Day] INT NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NULL,
    [CreatedDate] DATETIME2 NOT NULL,
    [ValidFrom] DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
    [ValidTo] DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [BMWSequence].[Audit_HolidayHours], DATA_CONSISTENCY_CHECK = ON))";

        var fileScript = @"CREATE TABLE [BMWSequence].[HolidayHours] (
    [Id] INT IDENTITY (1, 1) NOT NULL,
    [Month] INT NOT NULL,
    [Day] INT NOT NULL,
    [StartTime] TIME (0) NOT NULL,
    [EndTime] TIME (0) NULL,
    [CreatedDate] DATETIME2 (7) NOT NULL,
    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [BMWSequence].[Audit_HolidayHours], DATA_CONSISTENCY_CHECK=ON));";

        // Apply the same normalization pipeline that the database and file
        // model builders use for table scripts.
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void NormalizeForComparison_Temporal_Table_Preserves_Generated_Always_Columns()
    {
        // Verify that GENERATED ALWAYS AS ROW START and GENERATED ALWAYS AS ROW END
        // clauses are preserved through normalization and not stripped away.
        var script = @"CREATE TABLE [dbo].[TemporalTable]
(
    [Id] INT NOT NULL,
    [ValidFrom] DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
    [ValidTo] DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[TemporalTableHistory]))";

        var normalized = SqlScriptNormalizer.Normalize(script);
        var firstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(normalized);
        var stripped = SqlScriptNormalizer.StripInlineConstraints(firstBatch);
        var forComparison = SqlScriptNormalizer.NormalizeForComparison(stripped);

        Assert.Contains("GENERATED ALWAYS AS ROW START", forComparison);
        Assert.Contains("GENERATED ALWAYS AS ROW END", forComparison);
        Assert.Contains("PERIOD FOR SYSTEM_TIME", forComparison);
        Assert.Contains("SYSTEM_VERSIONING = ON", forComparison);
    }

    [Fact]
    public void NormalizeForComparison_Temporal_Table_Detects_Missing_Temporal_Clauses()
    {
        // Verify that a non-temporal table script is NOT equal to a temporal table script.
        // This ensures we don't accidentally normalize away real temporal table differences.
        var nonTemporalScript = @"CREATE TABLE [BMWSequence].[HolidayHours]
(
    [Id] INT IDENTITY(1,1) NOT NULL,
    [Month] INT NOT NULL,
    [Day] INT NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NULL,
    [CreatedDate] DATETIME2 NOT NULL,
    [ValidFrom] DATETIME2 NOT NULL,
    [ValidTo] DATETIME2 NOT NULL
)";

        var temporalScript = @"CREATE TABLE [BMWSequence].[HolidayHours]
(
    [Id] INT IDENTITY(1,1) NOT NULL,
    [Month] INT NOT NULL,
    [Day] INT NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NULL,
    [CreatedDate] DATETIME2 NOT NULL,
    [ValidFrom] DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
    [ValidTo] DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [BMWSequence].[Audit_HolidayHours]))";

        var nonTemporalNormalized = SqlScriptNormalizer.Normalize(nonTemporalScript);
        var nonTemporalFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(nonTemporalNormalized);
        var nonTemporalStripped = SqlScriptNormalizer.StripInlineConstraints(nonTemporalFirstBatch);
        var nonTemporalForComparison = SqlScriptNormalizer.NormalizeForComparison(nonTemporalStripped);

        var temporalNormalized = SqlScriptNormalizer.Normalize(temporalScript);
        var temporalFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(temporalNormalized);
        var temporalStripped = SqlScriptNormalizer.StripInlineConstraints(temporalFirstBatch);
        var temporalForComparison = SqlScriptNormalizer.NormalizeForComparison(temporalStripped);

        // The scripts should NOT be equal - real temporal differences must be detected
        Assert.NotEqual(nonTemporalForComparison, temporalForComparison);
    }

    [Fact]
    public void NormalizeIndexForComparison_Collapses_Multiline_Index_To_Single_Line()
    {
        // Arrange - database generates single-line script
        var databaseScript = "CREATE NONCLUSTERED INDEX [Missing_IXNC_CLOCOrders_LiftsRemaining_8FD79] ON [dbo].[CLOCOrders]([LiftsRemaining] ASC) INCLUDE([CLOCOrderID], [CLOC], [InventoryID], [OrderDate], [HotOrder])";

        // File has multi-line script with indentation
        var fileScript = @"CREATE NONCLUSTERED INDEX [Missing_IXNC_CLOCOrders_LiftsRemaining_8FD79]
    ON [dbo].[CLOCOrders]([LiftsRemaining] ASC)
    INCLUDE([CLOCOrderID], [CLOC], [InventoryID], [OrderDate], [HotOrder])";

        // Act
        var normalizedDb = SqlScriptNormalizer.NormalizeIndexForComparison(databaseScript);
        var normalizedFile = SqlScriptNormalizer.NormalizeIndexForComparison(fileScript);

        // Assert - both should normalize to the same single-line script
        Assert.Equal(normalizedDb, normalizedFile);
    }

    [Fact]
    public void NormalizeIndexForComparison_Preserves_Semantic_Differences()
    {
        // Arrange - different index with different columns
        var index1 = "CREATE NONCLUSTERED INDEX [IX_Test] ON [dbo].[Orders]([OrderDate] ASC)";
        var index2 = "CREATE NONCLUSTERED INDEX [IX_Test] ON [dbo].[Orders]([CustomerID] ASC)";

        // Act
        var normalized1 = SqlScriptNormalizer.NormalizeIndexForComparison(index1);
        var normalized2 = SqlScriptNormalizer.NormalizeIndexForComparison(index2);

        // Assert - real semantic differences must be preserved
        Assert.NotEqual(normalized1, normalized2);
    }

    [Fact]
    public void NormalizeForComparison_Memory_Optimized_Table_With_Different_Formatting_Matches()
    {
        // Arrange - database-generated script (as TableScriptBuilder would produce it)
        var databaseScript = @"CREATE TABLE [ProcKey].[Android_BoxMovePartNumberInfo]
(
    [ExecutionID] UNIQUEIDENTIFIER NOT NULL,
    [ParameterID] UNIQUEIDENTIFIER NOT NULL,
    [PartNumber] NVARCHAR(50) NULL,
    [PlantID] INT NULL,
    [PutawayNotes] NVARCHAR(255) NULL,
    [Location] NVARCHAR(50) NULL,
    [DeliverTo] NVARCHAR(50) NULL,
    [CLOC] NVARCHAR(50) NULL,
    [ExecutionDate] DATETIME NULL
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)";

        // File script with trailing comma and different WITH formatting
        var fileScript = @"CREATE TABLE [ProcKey].[Android_BoxMovePartNumberInfo](
    [ExecutionID] UNIQUEIDENTIFIER NOT NULL,
    [ParameterID] UNIQUEIDENTIFIER NOT NULL,
    [PartNumber] NVARCHAR(50) NULL,
    [PlantID] INT NULL,
    [PutawayNotes] NVARCHAR(255) NULL,
    [Location] NVARCHAR(50) NULL,
    [DeliverTo] NVARCHAR(50) NULL,
    [CLOC] NVARCHAR(50) NULL,
    [ExecutionDate] DATETIME NULL,
)
WITH(DURABILITY = SCHEMA_ONLY, MEMORY_OPTIMIZED = ON)";

        // Apply the same normalization pipeline
        var dbNormalized = SqlScriptNormalizer.Normalize(databaseScript);
        var dbFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(dbNormalized);
        var dbStripped = SqlScriptNormalizer.StripInlineConstraints(dbFirstBatch);
        var dbForComparison = SqlScriptNormalizer.NormalizeForComparison(dbStripped);

        var fileNormalized = SqlScriptNormalizer.Normalize(fileScript);
        var fileFirstBatch = SqlScriptNormalizer.TruncateAfterFirstGo(fileNormalized);
        var fileStripped = SqlScriptNormalizer.StripInlineConstraints(fileFirstBatch);
        var fileForComparison = SqlScriptNormalizer.NormalizeForComparison(fileStripped);

        // Assert - they should be equal after normalization
        Assert.Equal(dbForComparison, fileForComparison);
    }

    [Fact]
    public void NormalizeForComparison_Memory_Optimized_Table_Detects_Different_Durability()
    {
        // Arrange - SCHEMA_ONLY durability
        var schemaOnlyScript = @"CREATE TABLE [ProcKey].[TestTable]
(
    [ID] INT NOT NULL
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)";

        // SCHEMA_AND_DATA durability (the default but explicitly specified)
        var schemaAndDataScript = @"CREATE TABLE [ProcKey].[TestTable]
(
    [ID] INT NOT NULL
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA)";

        // Apply the same normalization pipeline
        var schemaOnlyNormalized = SqlScriptNormalizer.NormalizeForComparison(schemaOnlyScript);
        var schemaAndDataNormalized = SqlScriptNormalizer.NormalizeForComparison(schemaAndDataScript);

        // Assert - different durability options must be detected as different
        Assert.NotEqual(schemaOnlyNormalized, schemaAndDataNormalized);
    }

    [Fact]
    public void NormalizeForComparison_Memory_Optimized_Table_Detects_Missing_Memory_Optimized_Option()
    {
        // Arrange - table with memory-optimized options
        var memoryOptimizedScript = @"CREATE TABLE [ProcKey].[TestTable]
(
    [ID] INT NOT NULL
)
WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)";

        // Same table without memory-optimized options
        var regularTableScript = @"CREATE TABLE [ProcKey].[TestTable]
(
    [ID] INT NOT NULL
)";

        // Apply the same normalization pipeline
        var memOptNormalized = SqlScriptNormalizer.NormalizeForComparison(memoryOptimizedScript);
        var regularNormalized = SqlScriptNormalizer.NormalizeForComparison(regularTableScript);

        // Assert - presence/absence of memory-optimized options must be detected
        Assert.NotEqual(memOptNormalized, regularNormalized);
    }
}

