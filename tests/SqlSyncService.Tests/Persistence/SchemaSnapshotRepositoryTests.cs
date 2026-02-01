using LiteDB;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Persistence;

namespace SqlSyncService.Tests.Persistence;

public class SchemaSnapshotRepositoryTests
{
    private static SchemaSnapshotRepository CreateRepository()
    {
        var database = new LiteDatabase(new MemoryStream());
        var context = new LiteDbContext(database);
        return new SchemaSnapshotRepository(context);
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
}

