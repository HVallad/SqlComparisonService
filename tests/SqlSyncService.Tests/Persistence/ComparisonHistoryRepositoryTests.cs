using LiteDB;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Persistence;

namespace SqlSyncService.Tests.Persistence;

public class ComparisonHistoryRepositoryTests
{
    private static ComparisonHistoryRepository CreateRepository()
    {
        var database = new LiteDatabase(new MemoryStream());
        var context = new LiteDbContext(database);
        return new ComparisonHistoryRepository(context);
    }

    [Fact]
    public async Task Add_And_GetById_Roundtrips_Result()
    {
        // Arrange
        var repository = CreateRepository();
        var result = new ComparisonResult
        {
            SubscriptionId = Guid.NewGuid(),
            ComparedAt = DateTime.UtcNow,
            Status = ComparisonStatus.HasDifferences
        };

        // Act
        await repository.AddAsync(result);
        var loaded = await repository.GetByIdAsync(result.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(result.Id, loaded!.Id);
        Assert.Equal(ComparisonStatus.HasDifferences, loaded.Status);
    }

    [Fact]
    public async Task GetBySubscription_Returns_Limited_And_Ordered_Results()
    {
        // Arrange
        var repository = CreateRepository();
        var subscriptionId = Guid.NewGuid();

        var older = new ComparisonResult
        {
            SubscriptionId = subscriptionId,
            ComparedAt = DateTime.UtcNow.AddMinutes(-10),
            Status = ComparisonStatus.Synchronized
        };
        var middle = new ComparisonResult
        {
            SubscriptionId = subscriptionId,
            ComparedAt = DateTime.UtcNow.AddMinutes(-5),
            Status = ComparisonStatus.HasDifferences
        };
        var newer = new ComparisonResult
        {
            SubscriptionId = subscriptionId,
            ComparedAt = DateTime.UtcNow,
            Status = ComparisonStatus.Error
        };

        await repository.AddAsync(older);
        await repository.AddAsync(middle);
        await repository.AddAsync(newer);

        // Act
        var all = await repository.GetBySubscriptionAsync(subscriptionId);
        var topTwo = await repository.GetBySubscriptionAsync(subscriptionId, maxCount: 2);

        // Assert - ordering
        Assert.Equal(3, all.Count);
        Assert.Equal(ComparisonStatus.Error, all[0].Status);
        Assert.Equal(ComparisonStatus.HasDifferences, all[1].Status);
        Assert.Equal(ComparisonStatus.Synchronized, all[2].Status);

        // Assert - limiting
        Assert.Equal(2, topTwo.Count);
        Assert.Equal(ComparisonStatus.Error, topTwo[0].Status);
        Assert.Equal(ComparisonStatus.HasDifferences, topTwo[1].Status);
    }
}

