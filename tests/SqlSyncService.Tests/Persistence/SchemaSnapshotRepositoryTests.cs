using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Persistence;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SqlSyncService.Tests.Persistence;

public class SchemaSnapshotRepositoryTests
{
    private static SchemaSnapshotRepository CreateRepository()
    {
        var database = new LiteDatabase(new MemoryStream());
        var context = new LiteDbContext(database);
        return new SchemaSnapshotRepository(context, NullLogger<SchemaSnapshotRepository>.Instance);
    }

    [Fact]
    public async Task Add_And_GetById_Roundtrips_Snapshot()
    {
        // Arrange
        var repository = CreateRepository();
        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow,
            Hash = "hash-1"
        };

        // Act
        await repository.AddAsync(snapshot);
        var loaded = await repository.GetByIdAsync(snapshot.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Id, loaded!.Id);
        Assert.Equal("hash-1", loaded.Hash);
    }

    [Fact]
    public async Task GetBySubscription_Returns_Ordered_By_CapturedAt()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();
        var older = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddMinutes(-10),
            Hash = "old"
        };
        var newer = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            Hash = "new"
        };

        await repository.AddAsync(newer);
        await repository.AddAsync(older);

        // Act
        var list = await repository.GetBySubscriptionAsync(subscriptionId);

        // Assert
        Assert.Equal(2, list.Count);
        Assert.Equal("old", list[0].Hash);
        Assert.Equal("new", list[1].Hash);
    }

    [Fact]
    public async Task GetLatestForSubscription_Returns_Newest_Snapshot()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();
        var older = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddMinutes(-10),
            Hash = "old"
        };
        var newer = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            Hash = "new"
        };

        await repository.AddAsync(older);
        await repository.AddAsync(newer);

        // Act
        var latest = await repository.GetLatestForSubscriptionAsync(subscriptionId);

        // Assert
        Assert.NotNull(latest);
        Assert.Equal("new", latest!.Hash);
    }

    [Fact]
    public async Task DeleteAndDeleteForSubscription_Remove_Snapshots()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();
        var one = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow,
            Hash = "one"
        };
        var two = new SchemaSnapshot
        {
            SubscriptionId = subscriptionId,
            CapturedAt = DateTime.UtcNow.AddMinutes(1),
            Hash = "two"
        };

        await repository.AddAsync(one);
        await repository.AddAsync(two);

        // Act & Assert - Delete single
        var deleted = await repository.DeleteAsync(one.Id);
        Assert.True(deleted);

        var remaining = await repository.GetBySubscriptionAsync(subscriptionId);
        Assert.Single(remaining);

        // Act & Assert - Delete for subscription
        await repository.DeleteForSubscriptionAsync(subscriptionId);
        var afterPurge = await repository.GetBySubscriptionAsync(subscriptionId);
        Assert.Empty(afterPurge);
    }

    [Fact]
    public async Task UpdateObjectsAsync_Replaces_Function_When_Type_Changes()
    {
        // Arrange
        var repository = CreateRepository();
        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow,
            NormalizationVersion = SchemaSnapshot.CurrentNormalizationVersion,
            Objects = new List<SchemaObjectSummary>
            {
                new()
                {
                    SchemaName = "dbo",
                    ObjectName = "MyFunc",
                    ObjectType = SqlObjectType.ScalarFunction,
                    DefinitionHash = "old-hash",
                    DefinitionScript = "CREATE FUNCTION [dbo].[MyFunc]() RETURNS INT AS BEGIN RETURN 1 END",
                    ModifiedDate = DateTime.UtcNow.AddMinutes(-10)
                }
            }
        };

        await repository.AddAsync(snapshot);

        var updated = new SchemaObjectSummary
        {
            SchemaName = "dbo",
            ObjectName = "MyFunc",
            ObjectType = SqlObjectType.TableValuedFunction,
            DefinitionHash = "new-hash",
            DefinitionScript = "CREATE FUNCTION [dbo].[MyFunc]() RETURNS TABLE AS RETURN SELECT 1 AS Value;",
            ModifiedDate = DateTime.UtcNow
        };

        // Act
        await repository.UpdateObjectsAsync(snapshot.Id, new[] { updated });
        var loaded = await repository.GetByIdAsync(snapshot.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Objects);
        var obj = loaded.Objects[0];
        Assert.Equal("dbo", obj.SchemaName);
        Assert.Equal("MyFunc", obj.ObjectName);
        Assert.Equal(SqlObjectType.TableValuedFunction, obj.ObjectType);
        // For snapshots created with the current normalization pipeline, the
        // repository should not re-normalize definitions/hashes on load. The
        // values persisted via UpdateObjectsAsync should round-trip unchanged.
        Assert.Equal(updated.DefinitionScript, obj.DefinitionScript);
        Assert.Equal(updated.DefinitionHash, obj.DefinitionHash);
    }

    [Fact]
    public async Task RemoveObjectAsync_Removes_Function_When_Type_Changes()
    {
        // Arrange
        var repository = CreateRepository();
        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow,
            NormalizationVersion = SchemaSnapshot.CurrentNormalizationVersion,
            Objects = new List<SchemaObjectSummary>
            {
                new()
                {
                    SchemaName = "dbo",
                    ObjectName = "MyFunc",
                    ObjectType = SqlObjectType.ScalarFunction,
                    DefinitionHash = "hash",
                    DefinitionScript = "CREATE FUNCTION [dbo].[MyFunc]() RETURNS INT AS BEGIN RETURN 1 END"
                }
            }
        };

        await repository.AddAsync(snapshot);

        // Act - request removal using a different function classification
        await repository.RemoveObjectAsync(snapshot.Id, "dbo", "MyFunc", SqlObjectType.TableValuedFunction);
        var loaded = await repository.GetByIdAsync(snapshot.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Objects);
    }

    [Fact]
    public async Task GetById_Normalizes_Definitions_And_Recomputes_Object_And_Snapshot_Hashes()
    {
        // Arrange: create a snapshot that mimics legacy state with unnormalized
        // definition scripts and placeholder hashes.
        var repository = CreateRepository();
        const string tableScript = @"CREATE TABLE [Translator].[TranslatorConfig] (
	    [Id] INT NOT NULL,
	    [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
	    [ValidTo] DATETIME2 (7) GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
	    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
	);";

        const string indexScript = @"CREATE NONCLUSTERED INDEX [IX_TranslatorConfig_Id]
	ON [Translator].[TranslatorConfig] ([Id] ASC)
	WHERE [Id] IS NOT NULL;";

        var snapshot = new SchemaSnapshot
        {
            SubscriptionId = Guid.NewGuid(),
            CapturedAt = DateTime.UtcNow.AddMinutes(-5),
            Hash = "legacy-snapshot-hash",
            Objects = new List<SchemaObjectSummary>
                {
                    new()
                    {
                        SchemaName = "Translator",
                        ObjectName = "TranslatorConfig",
                        ObjectType = SqlObjectType.Table,
                        DefinitionScript = tableScript,
                        DefinitionHash = "legacy-table-hash",
                        ModifiedDate = DateTime.UtcNow.AddMinutes(-10)
                    },
                    new()
                    {
                        SchemaName = "Translator",
                        ObjectName = "TranslatorConfig.IX_TranslatorConfig_Id",
                        ObjectType = SqlObjectType.Index,
                        DefinitionScript = indexScript,
                        DefinitionHash = "legacy-index-hash",
                        ModifiedDate = DateTime.UtcNow.AddMinutes(-8)
                    }
                }
        };

        await repository.AddAsync(snapshot);

        // Act: loading the snapshot should run our legacy normalization pipeline
        // which now also re-normalizes definition scripts and hashes.
        var loaded = await repository.GetByIdAsync(snapshot.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Id, loaded!.Id);
        Assert.Equal(2, loaded.Objects.Count);
        Assert.Equal(SchemaSnapshot.CurrentNormalizationVersion, loaded.NormalizationVersion);

        var table = Assert.Single(loaded.Objects.Where(o => o.ObjectType == SqlObjectType.Table));
        var index = Assert.Single(loaded.Objects.Where(o => o.ObjectType == SqlObjectType.Index));

        var expectedTableDefinition = SqlScriptNormalizer.NormalizeForComparison(tableScript);
        var expectedTableHash = ComputeSha256(Encoding.UTF8.GetBytes(expectedTableDefinition));
        Assert.Equal(expectedTableDefinition, table.DefinitionScript);
        Assert.Equal(expectedTableHash, table.DefinitionHash);

        var expectedIndexDefinition = SqlScriptNormalizer.NormalizeIndexForComparison(indexScript);
        var expectedIndexHash = ComputeSha256(Encoding.UTF8.GetBytes(expectedIndexDefinition));
        Assert.Equal(expectedIndexDefinition, index.DefinitionScript);
        Assert.Equal(expectedIndexHash, index.DefinitionHash);

        // Snapshot-level hash should be recomputed from the normalized object hashes
        var orderedHashes = loaded.Objects
            .OrderBy(o => o.ObjectType.ToString())
            .ThenBy(o => o.SchemaName)
            .ThenBy(o => o.ObjectName)
            .Select(o => o.DefinitionHash);
        var combined = string.Join("|", orderedHashes);
        var expectedSnapshotHash = ComputeSha256(Encoding.UTF8.GetBytes(combined));
        Assert.Equal(expectedSnapshotHash, loaded.Hash);
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}

