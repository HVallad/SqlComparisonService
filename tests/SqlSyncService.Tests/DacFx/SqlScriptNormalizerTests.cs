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

        Assert.Contains("NUMERIC (18, 2) NULL", forComparison);
    }

}

