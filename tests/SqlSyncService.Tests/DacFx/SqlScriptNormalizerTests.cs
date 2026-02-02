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
}

