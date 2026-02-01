using LiteDB;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Persistence;

namespace SqlSyncService.Tests.Persistence;

public class PendingChangeRepositoryTests
{
    private static PendingChangeRepository CreateRepository()
    {
        var database = new LiteDatabase(new MemoryStream());
        var context = new LiteDbContext(database);
        return new PendingChangeRepository(context);
    }

    [Fact]
    public async Task Add_And_GetPendingForSubscription_Returns_Unprocessed_Changes()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();

        var processed = new DetectedChange
        {
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow.AddMinutes(-5),
            IsProcessed = true,
            ObjectIdentifier = "processed"
        };

        var pending1 = new DetectedChange
        {
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow.AddMinutes(-2),
            IsProcessed = false,
            ObjectIdentifier = "pending1"
        };

        var pending2 = new DetectedChange
        {
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow,
            IsProcessed = false,
            ObjectIdentifier = "pending2"
        };

        await repository.AddAsync(processed);
        await repository.AddAsync(pending1);
        await repository.AddAsync(pending2);

        // Act
        var pending = await repository.GetPendingForSubscriptionAsync(subscriptionId);

        // Assert
        Assert.Equal(2, pending.Count);
        Assert.Equal("pending1", pending[0].ObjectIdentifier);
        Assert.Equal("pending2", pending[1].ObjectIdentifier);
    }

    [Fact]
    public async Task MarkAsProcessed_Excludes_Change_From_Pending_List()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();
        var change = new DetectedChange
        {
            SubscriptionId = subscriptionId,
            DetectedAt = DateTime.UtcNow,
            IsProcessed = false,
            ObjectIdentifier = "to-process"
        };

        await repository.AddAsync(change);

        // Act
        await repository.MarkAsProcessedAsync(change.Id);
        var pending = await repository.GetPendingForSubscriptionAsync(subscriptionId);

        // Assert
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Delete_Removes_Change()
    {
        // Arrange
        var repository = CreateRepository();
        var change = new DetectedChange
        {
            SubscriptionId = Guid.NewGuid(),
            DetectedAt = DateTime.UtcNow,
            IsProcessed = false,
            ObjectIdentifier = "to-delete"
        };

        await repository.AddAsync(change);

        // Act
        var deleted = await repository.DeleteAsync(change.Id);
        var pending = await repository.GetPendingForSubscriptionAsync(change.SubscriptionId);

        // Assert
        Assert.True(deleted);
        Assert.Empty(pending);
    }
}

